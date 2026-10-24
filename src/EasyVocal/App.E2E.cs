using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using EasyVocal.Core.Dsp;
using EasyVocal.Core.Engine;
using EasyVocal.Core.Hotkeys;
using EasyVocal.Core.Text;
using EasyVocal.E2E;

namespace EasyVocal;

/// <summary>
/// <c>--data &lt;folder&gt; --e2e</c>: the real app (controller, engine, voice chain, mixer, windows) on test devices
/// (<see cref="TestAudioDevices"/>): a microphone that plays a known vowel in real time, and headphones and VB-CABLE
/// that record what they get. It clicks the real buttons and cards, opens the real dialogs, and checks the recorded
/// audio. VB-CABLE is missing at first and "installed" during the run. Its last step quits the normal way, starts a
/// second run (<c>--e2e-relaunch</c>) on the same folder that checks everything came back, and waits for it.
/// Each step is logged PASS/FAIL to <c>%TEMP%\easyvocal_e2e.log</c> and <c>&lt;data&gt;\e2e.log</c>; exit code 0 only
/// when every step of both runs passed.
/// </summary>
public partial class App
{
    private const int Sr = AudioFormat.SampleRate;
    private static readonly string E2ETempLog = Path.Combine(Path.GetTempPath(), "easyvocal_e2e.log");
    private static readonly int LimiterLag = new Limiter(Sr).LatencySamples;
    private static readonly MethodInfo ButtonOnClick =
        typeof(ButtonBase).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private TestAudioDevices? e2eDevices;
    private string e2eData = "";
    private int e2ePassed, e2eFailed;
    private Timer? e2eWatchdog;

    /// <summary>A failed check: the step stops and is logged FAIL with this text.</summary>
    private sealed class E2EFailure(string message) : Exception(message);

    private void RunE2E(string dataFolder, bool relaunch)
    {
        e2eData = dataFolder;
        if (!relaunch)
        {
            TryDeleteFile(E2ETempLog);
            TryDeleteFile(Path.Combine(dataFolder, "e2e.log"));
        }
        // Never hang: a dialog that wouldn't close must not keep the test (and dotnet test) waiting forever.
        int limit = relaunch ? 60 : 110;
        e2eWatchdog = new Timer(_ =>
        {
            E2ELog($"FAIL watchdog: the {(relaunch ? "relaunched " : "")}e2e run took longer than {limit} s");
            Environment.Exit(1);
        }, null, TimeSpan.FromSeconds(limit), Timeout.InfiniteTimeSpan);

        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, async () =>
        {
            try
            {
                if (relaunch) await E2EAfterRelaunch();
                else await E2EFirstRun();
            }
            catch (Exception ex)
            {
                // Something outside a step (the app never started…): fail now instead of waiting for the watchdog.
                e2eFailed++;
                E2ELog("FAIL the e2e run stopped: " + ex);
                Environment.ExitCode = 1;
                e2eWatchdog?.Dispose();
                if (!quitting) Quit(ask: false);
            }
        });
    }

    // ---------- the first run ----------

    private async Task E2EFirstRun()
    {
        E2ELog("EasyVocal e2e test (language " + L.Language + ", data " + e2eData + ")");
        var (c, main, d) = await E2EStarted(EngineState.NoVirtualCable);
        double normalShare = 1; // measured in step 1 (the clean vowel is all harmonics)
        string? hotkey = null;
        string? myVoice = null;

        await Step("9  no VB-CABLE: monitor-only, banner, Hear me preview, sounds refused, headphones back, cable attaches", async () =>
        {
            Expect(c.State == EngineState.NoVirtualCable && c.MonitorOnly, "the session runs without the virtual microphone (monitor only)");
            Expect(main.Banner.Visibility == Visibility.Visible
                   && main.BannerText.Text == L.T("Virtual microphone not found. You can still try the voices with 🎧 Hear me.")
                   && main.FixButton.Visibility == Visibility.Visible, "the banner says the virtual microphone is missing and offers Fix");
            Expect(d.Latest(TestAudioDevices.CableId) == null, "nothing was opened as the call device");
            // The banner's Fix: setup lists, watches and opens the same devices as the engine (its level bar listens to the test mic).
            await Modal(w => w is SetupWindow, () => Click(main.FixButton), async w =>
            {
                var setup = (SetupWindow)w;
                Expect(setup.CableMissingPanel.Visibility == Visibility.Visible, "setup sees that VB-CABLE is missing");
                Expect(HasDevice(setup.MicBox, TestAudioDevices.MicId), "setup lists the test microphone");
                await WaitUntil(() => setup.MeterBar.Width > 0.3 * setup.MeterTrack.ActualWidth, 3000, "setup's level bar to move with the voice");
                Note(Inv($"setup's level bar {setup.MeterBar.Width:0}/{setup.MeterTrack.ActualWidth:0} px on the test microphone"));
            }, "the setup window");
            Expect(c.State == EngineState.NoVirtualCable && c.MonitorOnly, "closing setup leaves the session as it was");
            await WaitUntil(() => c.IsSoundLoaded("airhorn"), 8000, "the starter sounds to load");
            ClickSound(main, "airhorn");
            Expect(!c.IsPlaying("airhorn"), "a sound is refused while nothing can reach a call");

            Click(main.HearMeButton);
            Expect(c.HearMe && main.HearMeButton.IsChecked == true, "Hear me turns on");
            await ExpectCopy(Phones, "the headphones play the voice (Hear me without VB-CABLE)");

            var firstPhones = Phones();
            d.SetHeadphones(false); // unplugged
            await WaitUntil(() => c.MonitorLost, 4000, "the engine to notice the unplugged headphones");
            d.SetHeadphones(true);
            await WaitUntil(() => !ReferenceEquals(d.Latest(TestAudioDevices.HeadphonesId), firstPhones) && !c.MonitorLost, 8000,
                "the headphones to be opened again in the monitor-only session");
            Expect(firstPhones.Disposed, "the unplugged headphones were closed");
            Expect(c.State == EngineState.NoVirtualCable, "still waiting for VB-CABLE");
            await ExpectCopy(Phones, "the plugged-back headphones play the voice");

            var mic = d.Mic;
            d.SetCable(true); // "installed"
            await WaitUntil(() => c.State == EngineState.Running, 8000, "VB-CABLE to be attached");
            await WaitUntil(() => main.Banner.Visibility != Visibility.Visible, 3000, "the banner to go away");
            Expect(ReferenceEquals(d.Mic, mic), "attaching the cable kept the microphone running");
            await ExpectCopy(Call, "the call gets the voice once VB-CABLE is there");
            Click(main.HearMeButton);
            Expect(!c.HearMe, "Hear me turns off");
        }, fromHere: true);

        await Step("1  Normal voice: the call gets the mic's voice, the level meter moves", async () =>
        {
            await Baseline(c, main);
            var (got, sent) = await ExpectCopy(Call, "the call is the microphone's voice (limiter delay only)");
            normalShare = E2ESignal.HarmonicShare(got);
            Note(Inv($"call RMS {E2ESignal.Rms(got):0.000} (mic {E2ESignal.Rms(sent):0.000}), on the 150 Hz harmonics {normalShare:P0}"));
            await WaitUntil(() => c.InputLevel > 0.35f, 3000, "the input level to show the voice");
            await WaitUntil(() => main.MeterBar.Width > 0.5 * main.MeterTrack.ActualWidth, 3000, "the level meter to fill");
            await WaitUntil(() => c.InputLevel < 0.01f, 3000, "the input level to fall in the pause");
            Note(Inv($"meter {main.MeterBar.Width:0}/{main.MeterTrack.ActualWidth:0} px while speaking"));
            await Modal(w => w is SettingsWindow, () => Click(main.SettingsButton), w =>
            {
                var settings = (SettingsWindow)w;
                Expect(HasDevice(settings.MicBox, TestAudioDevices.MicId) && HasDevice(settings.OutputBox, TestAudioDevices.CableId)
                       && HasDevice(settings.MonitorBox, TestAudioDevices.HeadphonesId), "Settings lists the devices the engine uses");
                return Task.CompletedTask;
            }, "the settings window");
        });

        await Step("2  voice cards: Chipmunk/Female raise the pitch, Male/Monster lower it, Robot moves energy off the voice", async () =>
        {
            foreach (var (id, low, high) in new[] { ("chipmunk", 200.0, 400.0), ("female", 175.0, 300.0), ("male", 60.0, 130.0), ("monster", 60.0, 125.0) })
            {
                ClickVoice(main, id);
                Expect(c.ActiveVoice.Id == id && c.FilterEnabled, id + " is the active voice");
                var (got, _) = await Record(Call, settleMs: 200);
                double hz = E2ESignal.Pitch(got);
                Note(Inv($"{id}: pitch {hz:0} Hz (mic 150 Hz)"));
                Expect(hz >= low && hz <= high, $"{id} pitch {hz:0} Hz is within {low:0}-{high:0} Hz");
            }
            ClickVoice(main, "robot");
            var (robot, _) = await Record(Call, settleMs: 200);
            double share = E2ESignal.HarmonicShare(robot);
            Note(Inv($"robot: {share:P0} on the voice's harmonics (Normal {normalShare:P0})"));
            Expect(share < 0.5 * normalShare, "Robot moves the energy off the voice's harmonics");
            Expect(!HasMenu(main.VoiceCard(AppController.NormalVoiceId), L.T("Hide")) && HasMenu(main.VoiceCard("robot"), L.T("Hide")),
                "Normal's card offers no Hide (other built-in voices do)");
        });

        await Step("3  ON/OFF and mute: OFF sends the clean voice, mute silences the voice but not the sounds", async () =>
        {
            if (c.ActiveVoice.Id != "robot") ClickVoice(main, "robot"); // where step 2 left it
            Click(main.OnOffButton);
            Expect(!c.FilterEnabled && main.OnOffButton.IsChecked == false, "the filter turns OFF");
            await ExpectCopy(Call, "OFF: the call gets the clean voice");
            Click(main.OnOffButton);
            Expect(c.FilterEnabled, "the filter turns ON again");
            var (robot, _) = await Record(Call, settleMs: 200);
            Expect(E2ESignal.HarmonicShare(robot) < 0.5 * normalShare, "ON: Robot is back");

            Click(main.MuteButton);
            Expect(c.MicMuted && main.MuteButton.IsChecked == true, "the mic is muted");
            var (muted, _) = await Record(Call);
            Note(Inv($"muted call RMS {E2ESignal.Rms(muted):0.00000}"));
            Expect(E2ESignal.Rms(muted) < 1e-4, "muted: no voice on the call");
            ClickSound(main, "airhorn");
            Expect(c.IsPlaying("airhorn"), "the airhorn plays while muted");
            var (horn, _) = await Record(Call, settleMs: 100, voiced: false);
            Note(Inv($"muted call with the airhorn RMS {E2ESignal.Rms(horn):0.000}"));
            Expect(E2ESignal.Rms(horn) > 0.05, "muted: sounds still reach the call");
            ClickSound(main, "airhorn");
            Click(main.MuteButton);
            Expect(!c.MicMuted, "unmuted");
            var (back, _) = await Record(Call, settleMs: 200);
            Expect(E2ESignal.Rms(back) > 0.05, "unmuted: the voice is back on the call");
        });

        await Step("4  sound cards: airhorn on the call, a second click stops it, Stop all", async () =>
        {
            await Baseline(c, main);
            ClickSound(main, "airhorn");
            Expect(c.IsPlaying("airhorn"), "the airhorn card starts it");
            var (got, sent) = await Record(Call, settleMs: 100, voiced: false);
            double horn = E2ESignal.Rms(E2ESignal.Minus(got, sent));
            Note(Inv($"airhorn on the call: RMS {horn:0.000} over the voice, 440 Hz {E2ESignal.Magnitude(E2ESignal.Minus(got, sent), 440):0.000}"));
            Expect(horn > 0.05, "the airhorn is heard on the call");
            ClickSound(main, "airhorn");
            Expect(!c.IsPlaying("airhorn"), "a second click stops it");
            await ExpectOnlyVoice(Call, "after the second click only the voice is left");

            ClickSound(main, "airhorn");
            ClickSound(main, "ding");
            Expect(c.IsPlaying("airhorn") && c.IsPlaying("ding"), "two sounds play together");
            Click(main.StopAllButton);
            Expect(!c.IsPlaying("airhorn") && !c.IsPlaying("ding"), "Stop all stops both");
            await ExpectOnlyVoice(Call, "after Stop all only the voice is left");
        });

        await Step("5  Hear me: the headphones get the voice (and come back when their stream stops); off, they only get the sounds", async () =>
        {
            await Baseline(c, main);
            Expect(c.HearSounds, "Hear sounds is on (the default)");
            Click(main.HearMeButton);
            await ExpectCopy(Phones, "Hear me on: the headphones get the voice");
            var phones = Phones();
            var mic = d.Mic;
            phones.Failing = true; // the stream stops working; no device is added or removed, so only the recovery timer helps
            await WaitUntil(() => c.MonitorLost, 2000, "the engine to notice the headphones stopped");
            await WaitUntil(() => !ReferenceEquals(Phones(), phones) && !c.MonitorLost, 5000, "the headphones to be opened again while running");
            Expect(phones.Disposed && ReferenceEquals(d.Mic, mic) && c.State == EngineState.Running, "the failed headphones were closed; the mic and the call kept running");
            await ExpectCopy(Phones, "the reopened headphones get the voice");
            Click(main.HearMeButton);
            var (quiet, _) = await Record(Phones);
            Expect(E2ESignal.Rms(quiet) < 1e-4, "Hear me off: no voice on the headphones");
            ClickSound(main, "airhorn");
            var (got, sent) = await Record(Phones, settleMs: 100);
            double corr = E2ESignal.Correlation(got, sent);
            Note(Inv($"headphones with the airhorn: RMS {E2ESignal.Rms(got):0.000}, correlation with the voice {corr:0.00}"));
            Expect(E2ESignal.Rms(got) > 0.05 && Math.Abs(corr) < 0.2, "the headphones get the sound but not the voice");
            ClickSound(main, "airhorn");
        });

        await Step("6  hotkey: set through the hotkey dialog, the hotkey's press switches the voice", async () =>
        {
            await Baseline(c, main);
            var chipmunk = c.Settings.Voices.First(v => v.Id == "chipmunk");
            await Modal(w => w is HotkeyCaptureDialog, () => ClickMenu(main.VoiceCard("chipmunk"), L.T("Set hotkey…")), w =>
            {
                var dialog = (HotkeyCaptureDialog)w;
                // Ctrl+1 unless another app on this PC holds it: then what a person would try next.
                foreach (var (mods, vk) in new[] { (HotkeyMods.Ctrl, 0x31), (HotkeyMods.Ctrl | HotkeyMods.Alt | HotkeyMods.Shift, 0x31), (HotkeyMods.Ctrl | HotkeyMods.Alt | HotkeyMods.Shift, 0x7A) })
                {
                    dialog.Simulate(mods, vk);
                    if (!dialog.SaveButton.IsEnabled) continue;
                    Click(dialog.SaveButton);
                    if (!dialog.IsVisible || dialog.DialogResult == true) return Task.CompletedTask;
                    Note("taken by another app: " + dialog.KeyText.Text);
                }
                throw new E2EFailure("no free hotkey could be saved");
            }, "the hotkey dialog");
            hotkey = chipmunk.Hotkey;
            Note("hotkey: " + hotkey);
            Expect(hotkey != null && !c.RefusedActions.Contains("voice:chipmunk") && c.RegisteredHotkeyCount == 1, "the hotkey is saved and registered");
            Expect(HasText(main.VoiceCard("chipmunk"), hotkey!), "the card shows its hotkey");
            Expect(c.ActiveVoice.Id == "normal", "Normal is still the active voice");
            Expect(c.SimulateHotkeyPress("voice:chipmunk"), "the hotkey is registered for Chipmunk");
            Expect(c.ActiveVoice.Id == "chipmunk", "pressing the hotkey switches to Chipmunk");
            var (got, _) = await Record(Call, settleMs: 200);
            Expect(E2ESignal.Pitch(got) > 200, "the call has the Chipmunk voice");
            for (int i = 0; i < 50; i++) { c.SetTyping(true); c.SetTyping(false); } // a text box gaining and losing the keyboard
            Expect(c.RegisteredHotkeyCount == 1 && c.HighestHotkeyId == 1, $"hotkey ids start over on every rebuild (highest {c.HighestHotkeyId})");
            Expect(c.SimulateHotkeyPress("voice:chipmunk"), "the hotkey still works after the rebuilds");
        });

        await Step("7  filter editor: live preview on the call and the headphones, Cancel puts everything back, hotkeys come back", async () =>
        {
            if (c.ActiveVoice.Id != "chipmunk") ClickVoice(main, "chipmunk"); // where step 6 left it
            if (c.HearMe) Click(main.HearMeButton);
            int hotkeys = c.RegisteredHotkeyCount;
            Expect(hotkeys >= 1, "a hotkey is registered to check on (the one from step 6)");
            await Modal(w => w is FilterEditorWindow, () => ClickMenu(main.VoiceCard("female"), L.T("Edit…")), async w =>
            {
                var editor = (FilterEditorWindow)w;
                Expect(editor.HearToggle.IsChecked == true && c.HearingMe && !c.HearMe, "the editor's own Hear me is on; the saved one is not touched");
                var (phones, _) = await Record(Phones, settleMs: 200);
                Expect(E2ESignal.Rms(phones) > 0.05, "the preview plays on the headphones");
                var (female, _) = await Record(Call, settleMs: 100);
                double before = E2ESignal.Pitch(female);
                var pitch = Sliders(editor)[0];
                pitch.Value = -0.8; // dragged towards "Deeper"
                var (deeper, _) = await Record(Call, settleMs: 250);
                double after = E2ESignal.Pitch(deeper);
                Note(Inv($"preview pitch {before:0} Hz (Female copy) → {after:0} Hz after dragging Pitch"));
                Expect(before > 175 && after > 0 && after < 125, "moving the Pitch slider changes the voice on the call live");
                // As if the name box had kept the keyboard and never said it lost it.
                editor.HearToggle.Focus();
                c.SetTyping(true);
                Expect(c.RegisteredHotkeyCount == 0, "the hotkeys are let go while typing");
                Click(editor.CancelButton);
                Expect(!editor.IsVisible, "Cancel closes the editor");
            }, "the filter editor");
            await Task.Delay(200);
            Expect(!c.IsTyping && c.RegisteredHotkeyCount == hotkeys, "the hotkeys are registered again after the editor closed");
            Expect(c.ActiveVoice.Id == "chipmunk" && !c.HearMe && !c.HearingMe, "Cancel: back to the active voice, the saved Hear me unchanged");
            var (got, _) = await Record(Call, settleMs: 200);
            Expect(E2ESignal.Pitch(got) > 200, "the call has the Chipmunk voice again");
            var (quiet, _) = await Record(Phones);
            Expect(E2ESignal.Rms(quiet) < 1e-4, "the headphones are quiet again");
            Expect(c.Settings.Voices.All(v => v.BuiltIn || v.Name != L.F("{0} copy", L.T("Female"))), "Cancel added no voice");

            // A filter of the person's own (used by the relaunch).
            await Modal(w => w is FilterEditorWindow, () => ClickMenu(main.VoiceCard("normal"), L.T("Edit…")), w =>
            {
                var editor = (FilterEditorWindow)w;
                editor.NameBox.Text = "E2E voice";
                Sliders(editor)[2].Value = 0.6; // Robot
                Click(editor.SaveButton);
                return Task.CompletedTask;
            }, "the filter editor (save)");
            var mine = c.Settings.Voices.FirstOrDefault(v => v.Name == "E2E voice");
            Expect(mine is { BuiltIn: false } && Math.Abs(mine.Sliders.Robot - 0.6) < 1e-9 && main.VoiceCard(mine.Id) != null, "Save adds the new filter and its card");
            myVoice = mine!.Id;
        });

        await Step("8  custom sound: added from a WAV file, plays on the call, deleted with its file and card", async () =>
        {
            await Baseline(c, main);
            var tone = await AddSoundFromFile(c, main, "e2e tone.wav", 1000, "E2E tone");
            var file = c.SoundPath(tone.FileName)!;
            ClickSound(main, tone.Id);
            Expect(c.IsPlaying(tone.Id), "its card plays it");
            var (got, sent) = await Record(Call, settleMs: 100, voiced: false);
            double at1k = E2ESignal.Magnitude(E2ESignal.Minus(got, sent), 1000);
            Note(Inv($"1 kHz tone on the call: {at1k:0.000} (file 0.5 × volume 0.8)"));
            Expect(at1k > 0.25, "the custom sound is heard on the call");
            ClickSound(main, tone.Id);
            Expect(!c.IsPlaying(tone.Id), "a second click stops it");
            await Modal(w => w.GetType() == typeof(Window) && ReferenceEquals(w.Owner, main), () => ClickMenu(main.SoundCard(tone.Id), L.T("Delete…")), w =>
            {
                Click(FindButton(w, L.T("Delete")));
                return Task.CompletedTask;
            }, "the delete question");
            Expect(c.Settings.Sounds.All(s => s.Id != tone.Id), "the sound is removed");
            Expect(!File.Exists(file), "its file is deleted from the Sounds folder");
            Expect(main.SoundCard(tone.Id) == null, "its card is gone");
        });

        await Step("10 microphone unplugged: banner, sounds stopped; plugged back: running again", async () =>
        {
            await Baseline(c, main);
            ClickSound(main, "airhorn");
            Expect(c.IsPlaying("airhorn"), "the airhorn plays");
            var mic = d.Mic;
            d.SetMic(false);
            await WaitUntil(() => c.State == EngineState.MicLost, 3000, "the engine to report the lost microphone");
            await WaitUntil(() => main.Banner.Visibility == Visibility.Visible && main.BannerText.Text == L.T("Microphone disconnected — reconnecting…"), 2000,
                "the 'Microphone disconnected' banner");
            Expect(main.MicButton.Visibility == Visibility.Visible, "the banner offers to choose a microphone");
            Expect(!c.IsPlaying("airhorn"), "the sounds were stopped");
            await Task.Delay(2500); // a recovery attempt or two while it is unplugged
            Expect(c.State == EngineState.MicLost, "still lost while the microphone is unplugged");
            d.SetMic(true);
            await WaitUntil(() => c.State == EngineState.Running, 8000, "the audio to run again");
            await WaitUntil(() => main.Banner.Visibility != Visibility.Visible, 3000, "the banner to go away");
            Expect(!ReferenceEquals(d.Mic, mic), "the microphone was opened again");
            await ExpectCopy(Call, "the call gets the voice again");
        });

        await Step("12 VB-CABLE takes Windows' defaults: the headphones stay real, banner + Fix, the install puts back what the person had", async () =>
        {
            await Baseline(c, main);
            string SoundInCable() => L.T("Your PC's sound is going into the virtual cable, so you can't hear it.");
            int calls = d.SetDefaultCalls.Length;

            // Windows' default playback device and microphone become the cable (nothing EasyVocal did).
            d.SetWindowsDefaults(TestAudioDevices.CableId, TestAudioDevices.CableMicId);
            await WaitUntil(() => c.SoundGoesToCable && main.Banner.Visibility == Visibility.Visible && main.BannerText.Text == SoundInCable(), 4000,
                "the 'sound is going into the virtual cable' banner");
            Expect(main.FixButton.Visibility == Visibility.Visible, "the banner offers Fix");
            Expect(d.SetDefaultCalls.Length == calls, "nothing is changed before Fix is pressed");
            await Task.Delay(700); // past the device-change settle: a restart would have happened by now
            Expect(c.State == EngineState.Running && c.MonitorInUse == TestAudioDevices.HeadphonesId,
                "the headphones are still the real ones while Windows' default is the cable");
            Click(main.HearMeButton);
            await ExpectCopy(Phones, "Hear me still plays on the real headphones");
            Click(main.HearMeButton);

            Click(main.FixButton);
            var fixedTo = d.SetDefaultCalls.Skip(calls).ToArray();
            Note("Fix set the defaults to: " + string.Join(", ", fixedTo));
            Expect(fixedTo.Contains(TestAudioDevices.HeadphonesId) && fixedTo.Contains(TestAudioDevices.MicId) && fixedTo.Length == 2,
                "Fix makes the real headphones and the real microphone Windows' defaults");
            await WaitUntil(() => d.DefaultOutput == TestAudioDevices.HeadphonesId && d.DefaultInput == TestAudioDevices.MicId
                                  && !c.SoundGoesToCable && main.Banner.Visibility != Visibility.Visible, 3000, "the banner to go away after Fix");

            // The install: setup remembers the defaults, the (fake) installer adds the cable and takes them, EasyVocal puts them back.
            d.SetCable(false);
            await WaitUntil(() => c.State == EngineState.NoVirtualCable, 4000, "the engine to see VB-CABLE gone");
            calls = d.SetDefaultCalls.Length;
            await Modal(w => w is SetupWindow, () => Click(main.FixButton), async w =>
            {
                var setup = (SetupWindow)w;
                Click(setup.InstallButton);
                await WaitUntil(() => setup.CableReadyPanel.Visibility == Visibility.Visible, 5000, "setup to see the installed cable");
                await WaitUntil(() => d.SetDefaultCalls.Length >= calls + 2 && d.DefaultOutput == TestAudioDevices.HeadphonesId && d.DefaultInput == TestAudioDevices.MicId, 5000,
                    "the (fake) installer to take the defaults and EasyVocal to put back the ones the person had");
                var restored = d.SetDefaultCalls.Skip(calls).ToArray();
                Note("after the install the defaults were put back to: " + string.Join(", ", restored));
                Expect(restored.Length == 2 && restored.Contains(TestAudioDevices.HeadphonesId) && restored.Contains(TestAudioDevices.MicId),
                    "exactly the headphones and the microphone from before the install are put back");
                Expect(c.Settings.PreCableOutputId == TestAudioDevices.HeadphonesId && c.Settings.PreCableInputId == TestAudioDevices.MicId,
                    "the defaults from before the install are remembered in the settings (for a cable that shows up after a restart)");
            }, "the setup window (install)");
            await WaitUntil(() => c.State == EngineState.Running && main.Banner.Visibility != Visibility.Visible, 8000, "the audio to run with the new cable");
            await ExpectCopy(Call, "the call gets the voice through the installed cable");
        });

        await Step("11 settings persistence (first run): a sound to keep, the custom voice, OFF, Hear me on, quit the normal way", async () =>
        {
            var keep = await AddSoundFromFile(c, main, "e2e keep.wav", 700, "E2E keep");
            ClickVoice(main, myVoice ?? throw new E2EFailure("step 7 made no voice"));
            Click(main.OnOffButton);
            Click(main.HearMeButton);
            Expect(!c.FilterEnabled && c.HearMe && c.ActiveVoice.Id == myVoice, "OFF, Hear me on, the custom voice active");
            File.WriteAllLines(Path.Combine(e2eData, "e2e-expect.txt"), new[] { "voice=" + myVoice, "sound=" + keep.Id, "hotkey=" + hotkey });
        });

        E2EQuitAndRelaunch();
    }

    /// <summary>
    /// Quits through the tray's Quit (the question is answered with "Don't ask again" ticked), then runs the second
    /// process on the same folder and waits for it. Synchronous on purpose: once the app shuts down, nothing awaited
    /// would come back.
    /// </summary>
    private void E2EQuitAndRelaunch()
    {
        bool answered = false;
        var watcher = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(40) };
        watcher.Tick += (_, _) =>
        {
            var ask = Windows.OfType<Window>().FirstOrDefault(w => w.GetType() == typeof(Window) && w.IsLoaded && w.IsVisible);
            if (ask == null) return;
            watcher.Stop();
            Click(FindChild<CheckBox>(ask, _ => true) ?? throw new E2EFailure("the quit question has no 'Don't ask again'"));
            Click(FindButton(ask, L.T("Quit")));
            answered = true;
        };
        watcher.Start();
        bool quitOk = false;
        try
        {
            Quit(ask: true); // what the tray's Quit does
            quitOk = answered && quitting && controller == null;
        }
        catch (Exception ex)
        {
            E2ELog("FAIL quitting: " + ex.Message);
        }
        watcher.Stop();
        Record11("11 quit the normal way: the question, 'Don't ask again', everything closed", quitOk);

        int code = RunRelaunch();
        Record11("11 relaunch on the same folder: settings, custom voice and sound, hotkeys back (exit code " + code + ")", code == 0);
        E2ELog(e2eFailed == 0 ? $"E2E test passed: {e2ePassed} steps (the relaunched run included)" : $"E2E test FAILED: {e2eFailed} of {e2ePassed + e2eFailed} steps");
        Environment.ExitCode = e2eFailed == 0 ? 0 : 1;
        e2eWatchdog?.Dispose();
    }

    private void Record11(string name, bool ok)
    {
        if (ok) e2ePassed++; else e2eFailed++;
        E2ELog((ok ? "PASS " : "FAIL ") + name);
    }

    private int RunRelaunch()
    {
        try
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
            var info = new ProcessStartInfo(exe) { UseShellExecute = false };
            info.ArgumentList.Add("--data");
            info.ArgumentList.Add(e2eData);
            info.ArgumentList.Add("--e2e-relaunch");
            using var p = Process.Start(info)!;
            if (p.WaitForExit(70_000)) return p.ExitCode;
            p.Kill(entireProcessTree: true);
            E2ELog("FAIL the relaunched run did not finish in 70 s");
            return -1;
        }
        catch (Exception ex)
        {
            E2ELog("FAIL relaunching: " + ex.Message);
            return -1;
        }
    }

    // ---------- the second run ----------

    private async Task E2EAfterRelaunch()
    {
        E2ELog("--- relaunched on the same folder ---");
        var (c, main, d) = await E2EStarted(EngineState.Running);
        await Step("11 after relaunch: settings, custom voice, custom sound and hotkey are back", async () =>
        {
            var expectFile = Path.Combine(e2eData, "e2e-expect.txt");
            Expect(File.Exists(expectFile), "the first run got as far as quitting (e2e-expect.txt)");
            var expect = File.ReadAllLines(expectFile).Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
            string voiceId = expect["voice"], soundId = expect["sound"], hotkey = expect["hotkey"];
            var s = c.Settings;
            Expect(!s.Enabled && s.HearMe && !s.ConfirmQuit && s.ActiveVoiceId == voiceId, "OFF, Hear me, 'Don't ask again' and the active voice were saved");
            Expect(main.OnOffButton.IsChecked == false && main.HearMeButton.IsChecked == true, "the top bar shows OFF and Hear me on");
            var voice = s.Voices.FirstOrDefault(v => v.Id == voiceId);
            Expect(voice is { Name: "E2E voice", BuiltIn: false } && Math.Abs(voice.Sliders.Robot - 0.6) < 1e-9 && main.VoiceCard(voiceId) != null,
                "the custom voice and its card are back");
            var sound = s.Sounds.FirstOrDefault(x => x.Id == soundId);
            Expect(sound is { Name: "E2E keep" } && File.Exists(c.SoundPath(sound.FileName)) && main.SoundCard(soundId) != null,
                "the custom sound, its file and its card are back");
            Expect(s.Voices.First(v => v.Id == "chipmunk").Hotkey == hotkey && c.RegisteredHotkeyCount == 1, "the hotkey is back and registered");
            Expect(s.PreCableOutputId == null && s.PreCableInputId == null, "the defaults remembered for the install were forgotten once the cable came");

            await ExpectCopy(Call, "OFF after the restart: the call gets the clean voice");
            await ExpectCopy(Phones, "Hear me after the restart: the headphones get the voice");

            Expect(c.SimulateHotkeyPress("voice:chipmunk") && c.ActiveVoice.Id == "chipmunk" && c.FilterEnabled, "the hotkey switches to Chipmunk (and turns the filter on)");
            var (chip, _) = await Record(Call, settleMs: 200);
            Expect(E2ESignal.Pitch(chip) > 200, "the call has the Chipmunk voice");

            Click(main.MuteButton);
            await WaitUntil(() => c.IsSoundLoaded(soundId), 5000, "the custom sound to load");
            ClickSound(main, soundId);
            var (got, _) = await Record(Call, settleMs: 100, voiced: false);
            double at700 = E2ESignal.Magnitude(got, 700);
            Note(Inv($"700 Hz on the (muted) call: {at700:0.000}"));
            Expect(c.IsPlaying(soundId) && at700 > 0.25, "the custom sound plays on the call");
        });

        E2ELog(e2eFailed == 0 ? $"Relaunch passed: {e2ePassed} step(s)" : $"Relaunch FAILED: {e2eFailed} step(s)");
        Environment.ExitCode = e2eFailed == 0 ? 0 : 1;
        e2eWatchdog?.Dispose();
        Quit(ask: false); // the question was checked in the first run ("Don't ask again" is checked above)
    }

    // ---------- steps, checks, log ----------

    /// <summary>Waits for the controller to start and settle in <paramref name="state"/>.</summary>
    private async Task<(AppController, MainWindow, TestAudioDevices)> E2EStarted(EngineState state)
    {
        var d = e2eDevices ?? throw new E2EFailure("no test devices");
        await WaitUntil(() => controller != null && window is { IsLoaded: true } && controller.State == state, 10000, "the audio to start (" + state + ")");
        return (controller!, window!, d);
    }

    /// <summary>
    /// Runs one step and logs PASS/FAIL. Unless <paramref name="fromHere"/>, the step first makes sure every test
    /// device is plugged in and the audio runs, so one failed step doesn't fail all the others.
    /// </summary>
    private async Task Step(string name, Func<Task> body, bool fromHere = false)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            if (!fromHere) await EnsureDevices();
            await body();
            e2ePassed++;
            E2ELog($"PASS {name} ({watch.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex)
        {
            e2eFailed++;
            E2ELog($"FAIL {name}: {(ex is E2EFailure ? ex.Message : ex.ToString())}");
        }
    }

    private async Task EnsureDevices()
    {
        var d = e2eDevices!;
        if (!d.MicPresent) d.SetMic(true);
        if (!d.HeadphonesPresent) d.SetHeadphones(true);
        if (!d.CablePresent) d.SetCable(true);
        await WaitUntil(() => controller is { State: EngineState.Running, MonitorLost: false } && window!.Banner.Visibility != Visibility.Visible,
            10000, "the audio to run with every device plugged in");
    }

    private static void Expect(bool ok, string what)
    {
        if (!ok) throw new E2EFailure(what);
    }

    private void Note(string text) => E2ELog("     · " + text);

    /// <summary>Numbers in the log the same on every PC (0.159, not 0,159).</summary>
    private static string Inv(FormattableString text) => FormattableString.Invariant(text);

    private void E2ELog(string line)
    {
        var text = DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + Environment.NewLine;
        foreach (var path in new[] { E2ETempLog, Path.Combine(e2eData, "e2e.log") })
        {
            try { File.AppendAllText(path, text); }
            catch (Exception) { /* the other log still has it */ }
        }
        AppLog.Info("E2E " + line);
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch (Exception) { }
    }

    private static async Task WaitUntil(Func<bool> done, int timeoutMs, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            if (watch.ElapsedMilliseconds > timeoutMs) throw new E2EFailure("timed out waiting for " + what);
            await Task.Delay(20);
        }
    }

    // ---------- clicking like a person ----------

    /// <summary>The same as a mouse click: <c>OnClick</c> (a toggle button flips first, then Click runs).</summary>
    private static void Click(ButtonBase? button)
    {
        Expect(button != null, "the button exists");
        Expect(button!.IsEnabled, "the button '" + button.Content + "' is enabled");
        ButtonOnClick.Invoke(button, null);
    }

    private static void ClickVoice(MainWindow main, string id)
    {
        main.Tabs.SelectedIndex = 0;
        main.UpdateLayout();
        var card = main.VoiceCard(id);
        Expect(card is { IsVisible: true }, "the voice card " + id + " is on screen");
        Click(card);
    }

    private static void ClickSound(MainWindow main, string id)
    {
        main.Tabs.SelectedIndex = 1;
        main.UpdateLayout();
        var card = main.SoundCard(id);
        Expect(card is { IsVisible: true }, "the sound card " + id + " is on screen");
        Click(card);
    }

    private static bool HasMenu(Button? card, string header) =>
        card?.ContextMenu?.Items.OfType<MenuItem>().Any(m => m.Header as string == header) == true;

    private static bool HasDevice(ComboBox box, string id) => box.Items.OfType<ComboBoxItem>().Any(i => i.Tag is DeviceInfo d && d.Id == id);

    /// <summary>Picks an item of a card's menu (right-click or "⋯").</summary>
    private static void ClickMenu(Button? card, string header)
    {
        var item = card?.ContextMenu?.Items.OfType<MenuItem>().FirstOrDefault(m => m.Header as string == header);
        Expect(item != null, "the card menu has '" + header + "'");
        item!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
    }

    /// <summary>
    /// Opens a dialog with <paramref name="open"/> (which may block in ShowDialog), runs <paramref name="whileOpen"/>
    /// once it is on screen, closes it if it is still open, and rethrows what failed inside.
    /// </summary>
    private async Task Modal(Func<Window, bool> match, Action open, Func<Window, Task> whileOpen, string what)
    {
        var done = new TaskCompletionSource<Exception?>();
        var watcher = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(40) };
        bool seen = false;
        watcher.Tick += async (_, _) =>
        {
            if (seen) return;
            var w = Windows.OfType<Window>().FirstOrDefault(x => !ReferenceEquals(x, window) && x.IsLoaded && x.IsVisible && match(x));
            if (w == null) return;
            seen = true;
            watcher.Stop();
            Exception? error = null;
            try
            {
                await Task.Delay(300); // let it draw (and an editor load its file)
                w.UpdateLayout();
                await whileOpen(w);
            }
            catch (Exception ex) { error = ex; }
            finally
            {
                if (w.IsVisible) w.Close();
                done.TrySetResult(error);
            }
        };
        watcher.Start();
        try
        {
            open();
        }
        catch (Exception ex)
        {
            watcher.Stop();
            throw new E2EFailure(what + " failed to open: " + ex.Message);
        }
        var first = await Task.WhenAny(done.Task, Task.Delay(15000));
        watcher.Stop();
        if (first != done.Task) throw new E2EFailure(what + " never appeared");
        if (done.Task.Result is { } failure) throw failure as E2EFailure ?? new E2EFailure(what + ": " + failure);
    }

    private static Button FindButton(DependencyObject root, string content) =>
        FindChild<Button>(root, b => b.Content as string == content) ?? throw new E2EFailure("no '" + content + "' button");

    private static T? FindChild<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T t && match(t)) return t;
            if (FindChild(child, match) is { } found) return found;
        }
        return null;
    }

    private static bool HasText(DependencyObject? root, string text) =>
        root != null && FindChild<TextBlock>(root, t => t.Text == text) != null;

    private static List<Slider> Sliders(FilterEditorWindow editor)
    {
        var list = new List<Slider>();
        foreach (var child in editor.SliderPanel.Children)
            if (child is Grid g) list.AddRange(g.Children.OfType<Slider>());
        Expect(list.Count == 7, "the filter editor has its seven sliders");
        return list;
    }

    /// <summary>Through the editor a dropped file opens: name it, Save; checks the card and the copied file.</summary>
    private async Task<Core.Model.SoundItem> AddSoundFromFile(AppController c, MainWindow main, string fileName, double hz, string name)
    {
        var path = Path.Combine(e2eData, fileName);
        E2ESignal.WriteToneWav(path, hz, 2.0, 0.5);
        await Modal(w => w is SoundEditorWindow, () => main.OpenSoundEditor(path), async w =>
        {
            var editor = (SoundEditorWindow)w;
            await WaitUntil(() => editor.SaveButton.IsEnabled, 5000, "the sound file to load in the editor");
            Expect(editor.NameBox.Text == Path.GetFileNameWithoutExtension(fileName), "the name starts as the file's name");
            editor.NameBox.Text = name;
            Click(editor.SaveButton);
        }, "the sound editor");
        var sound = c.Settings.Sounds.FirstOrDefault(s => s.Name == name);
        Expect(sound is { BuiltIn: false }, "the sound '" + name + "' is added");
        Expect(c.SoundPath(sound!.FileName) is { } copy && File.Exists(copy) && !string.Equals(copy, path, StringComparison.OrdinalIgnoreCase),
            "its file is copied into the Sounds folder");
        Expect(main.SoundCard(sound.Id) != null, "its card appears");
        return sound;
    }

    /// <summary>Normal voice, ON, not muted, Hear me off, no sound playing: the way every step starts.</summary>
    private static async Task Baseline(AppController c, MainWindow main)
    {
        if (c.MicMuted) Click(main.MuteButton);
        if (c.HearMe) Click(main.HearMeButton);
        if (c.ActiveVoice.Id != AppController.NormalVoiceId) ClickVoice(main, AppController.NormalVoiceId);
        if (!c.FilterEnabled) Click(main.OnOffButton);
        main.Tabs.SelectedIndex = 1;
        main.UpdateLayout();
        Click(main.StopAllButton);
        main.Tabs.SelectedIndex = 0;
        Expect(!c.MicMuted && !c.HearMe && c.FilterEnabled && c.ActiveVoice.Id == AppController.NormalVoiceId, "back to the starting point");
        await Task.Delay(100);
    }

    // ---------- listening ----------

    private CaptureOutput Call() => e2eDevices!.Latest(TestAudioDevices.CableId) ?? throw new E2EFailure("VB-CABLE was never opened");

    private CaptureOutput Phones() => e2eDevices!.Latest(TestAudioDevices.HeadphonesId) ?? throw new E2EFailure("the headphones were never opened");

    /// <summary>
    /// Waits <paramref name="settleMs"/>, then records <paramref name="seconds"/> of what a device gets, together with
    /// what the microphone played at the same moment (<c>sent</c>, shifted by the limiter's delay). Voiced: the window
    /// lies inside the vowel, away from the pauses.
    /// </summary>
    private async Task<(float[] got, float[] sent)> Record(Func<CaptureOutput> device, double seconds = 0.3, int settleMs = 150, bool voiced = true)
    {
        var d = e2eDevices!;
        var mic = d.Mic ?? throw new E2EFailure("no microphone is open");
        int n = (int)(seconds * Sr);
        long from = mic.Clock + (long)settleMs * Sr / 1000;
        if (voiced)
        {
            int loop = d.VoiceLoop.Length, lo = Sr * 60 / 1000, hi = (int)(E2ESignal.VoicedSeconds * Sr) - n;
            long pos = from % loop;
            if (pos < lo) from += lo - pos;
            else if (pos > hi) from += loop - pos + lo;
        }
        await WaitUntil(() => mic.Clock >= from + n + AudioFormat.BlockSize, 5000, "the microphone to play on");
        var dev = device();
        var got = dev.Read(mic, from, n) ?? throw new E2EFailure(dev.DeviceId + " recorded nothing at that moment");
        var sent = new float[n];
        for (int i = 0; i < n; i++)
        {
            long t = from - LimiterLag + i;
            sent[i] = t < 0 ? 0 : d.VoiceLoop[(int)(t % d.VoiceLoop.Length)];
        }
        return (got, sent);
    }

    /// <summary>The device gets exactly what the microphone played (clean voice, through the limiter).</summary>
    private async Task<(float[] got, float[] sent)> ExpectCopy(Func<CaptureOutput> device, string what)
    {
        var (got, sent) = await Record(device, settleMs: 200);
        double diff = E2ESignal.MaxAbsDiff(got, sent);
        Note(Inv($"{what}: largest difference {diff:0.000000} (RMS {E2ESignal.Rms(got):0.000})"));
        Expect(diff < 1e-3 && E2ESignal.Rms(got) > 0.05, what);
        return (got, sent);
    }

    /// <summary>The device gets the microphone's voice and nothing else (a sound has stopped).</summary>
    private async Task ExpectOnlyVoice(Func<CaptureOutput> device, string what)
    {
        var (got, sent) = await Record(device, settleMs: 450, voiced: false);
        double rest = E2ESignal.Rms(E2ESignal.Minus(got, sent));
        Note(Inv($"{what}: {rest:0.000000} RMS besides the voice"));
        Expect(rest < 2e-3, what);
    }
}
