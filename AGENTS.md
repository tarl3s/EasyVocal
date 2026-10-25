# AGENTS.md — EasyVocal

Context for AI coding assistants. `README.md` (English) and `README.pt-BR.md` are the player-facing description;
`docs/superpowers/specs/` holds the design and `docs/superpowers/plans/` the implementation plan.

## The project in one paragraph

EasyVocal is a **free, light Windows real-time voice changer + soundboard for any call app** (C# / WPF / .NET 8).
Microphone → filter → mixer (+ soundboard) → the virtual cable VB-CABLE, which the call app picks as its microphone.
Audience: people who want fun on calls, not audio people (buttons, emojis, plain sliders). MIT, public repo.
Owner speaks Brazilian Portuguese; code, comments, UI source text and commits are English.

## Build, run, test

```bash
dotnet build                       # 0 errors expected
dotnet test                        # whole suite (xUnit), including the E2E test below (builds the app first)
dotnet test --filter Category!=E2E # everything but the E2E test (~20 s instead of ~50 s)
dotnet run --project src/EasyVocal
dotnet run --project src/EasyVocal -- --data <folder> --smoketest   # opens every window, renders PNGs, exits 0
dotnet run --project src/EasyVocal -- --data <folder> --e2e         # end-to-end test on test audio devices, exits 0
tools/Publish-Release.ps1 -NoUpload   # self-contained single-file zip -> publish\EasyVocal-win-x64.zip (< 80 MB)
EV_VOICE_SAMPLES=<folder> dotnet test --filter RenderListeningSamples   # every built-in voice on real speech, as WAVs + README.txt (voice-samples/ is git-ignored)
```

App arguments (`App.xaml.cs`, parsed by `AppArgs.cs`): `--data <folder>` (use that folder instead of
`%APPDATA%\EasyVocal`), `--smoketest` (headless-ish UI check, PNGs and `easyvocal.log` go to the data folder),
`--e2e` (see below), `--tray` (start hidden in the tray), `--update-check` (a source build looks for a new release
anyway; it still never installs), `--update-now` (no window: download and install the latest release without asking,
log in `<data>\easyvocal.log` and `<data>\update.log`; exit 0 updated or already latest, 1 failed, 3 source build, 4
EasyVocal is running). The tests refuse to run without `--data` (exit code 2),
skip the first-run setup, never touch the real "Start with Windows" entry and don't hand over to (or wake) a running
EasyVocal. Tests create temp folders under `%TEMP%\EV_*` and must delete them.

**E2E test** (`App.E2E.cs`, devices in `E2E/`): the real app (controller, engine, voice chain, mixer, windows) with
`TestAudioDevices` swapped in through `AppController`'s `IAudioDevices` parameter: a microphone that plays a 150 Hz
vowel with pauses in real time on its own thread, and headphones and VB-CABLE that record what they get. It clicks
the real cards and buttons, opens the real dialogs, and checks the recorded audio (exact copy of the voice where the
voice should be clean, pitch, energy on the harmonics, sounds over the voice): no VB-CABLE then installed, Normal,
the voice cards, Settings and Setup listing the test devices, ON/OFF and mute, sound cards and Stop all, Hear me (and headphones whose stream stops coming back), a hotkey set through its dialog and pressed
(a real WM_HOTKEY), the filter editor (live preview, Cancel, hotkeys back), a custom sound added and deleted, 🎭 Sounds use the voice (a sound takes the voice and follows a voice change), the
mic unplugged and back, VB-CABLE taking Windows' default devices (the banner's Fix, the setup's Install with a fake installer putting back what was there), then quit the normal way and a second run (`--e2e-relaunch`) checks that everything came
back. PASS/FAIL per step in `%TEMP%\easyvocal_e2e.log` and `<data>\e2e.log`; exit 0 only when every step passed.
`EasyVocal.Tests/E2ETests.cs` (trait `Category=E2E`) runs the built exe this way (it passes with a "SKIPPED" note
when the exe isn't built). It shows its windows for ~35 s and holds Ctrl+1 (or a free fallback) meanwhile.

## Architecture map

| Area | Files |
|---|---|
| DSP (no WPF, no NAudio device code) | `src/EasyVocal.Core/Dsp/`: `Effects.cs` (echo, radio, distortion, robot, whisper), `VoiceEffects.cs` (growl, "Big room" reverb, bass/treble tone, vocoder), `VoiceShifter.cs` (pitch/formant TD-PSOLA, plus wobble, monotone and a voiced-only mode for the octave layer), `Biquad.cs` (incl. shelves), `Limiter.cs`, `VoiceChain.cs` (`VoiceSliders` → a table of parameters → shifter [+ octave layer] → growl → vocoder → robot → distortion → radio → whisper → echo → room → tone; slides/crossfades; `CreateFollower` = another signal through the same voice), `IAudioEffect.cs` (+ `IAmountEffect`), `AudioFormat.cs` |
| Engine | `Core/Engine/`: `AudioEngine.cs` (mic → chain → mixer → outputs; with `SoundsUseVoice` the mixer goes through a follower of the chain first, so mute / Hear me / Hear sounds still tell voice and sounds apart), `AudioInterfaces.cs` (device abstractions), `DeviceRules.cs` (finds the virtual cable, picks real devices), `DefaultDeviceRules.cs` (VB-CABLE taking Windows' default devices: remember, put back, Fix), `FormatConverter.cs` |
| Sounds | `Core/Sounds/`: `SoundLoader.cs` (WAV/AIFF/MP3 via NAudio, OGG via NVorbis, FLAC/M4A/AAC/WMA and the sound of MP4/MOV/WMV videos via Windows' Media Foundation; `Extensions`/`DialogPattern` feed the file pickers), `SoundWriter.cs` (16-bit WAV, MP3 through Media Foundation when Windows has the encoder), `AudioEdit.cs` (the sound editor's pure operations on mono 48 kHz: cut/trim/split, speed with WSOLA time-stretch or resample, pitch, gain/normalize, fades, reverse, echo, a voice run offline through `VoiceChain` with its latency removed and its tail kept (on a part it rings on over what follows, like the echo), remove silence), `AudioHistory.cs` (its undo/redo, 20 steps, ~256 MB cap), `SoundMixer.cs`, `StarterSoundSynth.cs` (the 10 starter sounds are generated by code), `LoadTickets.cs` (drops a background decode of a sound that changed meanwhile) |
| Model and storage | `Core/Model/`: `AppSettings.cs`, `BuiltIns.cs` (14 voices, each with its own signature, see below; starter sounds), `ItemOps.cs` (`ImportSound`: a picked file is copied into `Sounds`, a video only as a WAV of its sound; also the edited-copy rules: `WriteSoundFile`, `UseEditedFile`, `BackToOriginal`, `AddSplitSounds`; a file stays while any sound uses it as `FileName` or `OriginalFileName`), `Core/Layout/WindowFit.cs` (`WindowBox`, clamp a window into a screen's working area, pick the screen, does a remembered place still fit); `Core/Storage/SettingsStore.cs` (`%APPDATA%\EasyVocal\settings.json`, `Sounds\`) |
| Hotkeys and text | `Core/Hotkeys/` (`Hotkey.cs`, `HotkeyMap.cs`), `Core/Text/Localizer.cs` |
| App (WPF) | `src/EasyVocal/`: `App.xaml(.cs)` (startup, single instance, args), `AppController.cs` (wires engine, settings, hotkeys), `MainWindow`, `SettingsWindow`, `SetupWindow` (first run, VB-CABLE install via `VbCableInstaller.cs` + `Authenticode.cs`), `FilterEditorWindow`, `SoundEditorWindow`, `AudioEditorWindow` ("✂ Edit audio…": wave, selection, headphone playback with playhead, the `AudioEdit` tools, Save / Back to original / Save a copy as…) + `WaveformView.cs` (the wave control: cached min/max columns, ruler, zoom, drag-select), `WindowPlacer.cs` (every window: never bigger than or off the screen, place remembered in `AppSettings.WindowPlacements`), `HotkeyCaptureDialog`, `Tray.cs`, `StartupRegistration.cs`, `Loc.cs` + `Lang/pt-BR.json`, `Theme.xaml`, `Audio/` (WASAPI mic, speaker, meter, preview; `AudioDevices.cs` = the `IAudioDevices` seam the controller opens devices through and reads/sets Windows' defaults with; `DefaultDevices.cs` = IPolicyConfig, never called by the tests: the smoke test gets read-only defaults and no installer), `Hotkeys/GlobalHotkeys.cs`, `App.SmokeTest.cs` + `App.SmokeTest.Editor.cs` (the audio editor's tools, every window at its smallest size), `App.E2E.cs` + `E2E/` (test devices, signal and measurements) |
| Updater | `src/EasyVocal/Updater.cs` (no WPF, compiled into the tests too: `AppVersion` (tags, semver order), `UpdateRules` (GitHub's release JSON, the zip for this PC, when to offer: newer, not draft/pre-release, not the skipped version unless "Check now"; `IsSourceBuild` = an `EasyVocal.sln` or `src\EasyVocal\EasyVocal.csproj` above the exe), `ReleaseNotes` (Markdown to plain lines), `UpdatePackage` (zip checks: zip-slip, size, an `EasyVocal.exe` that starts with MZ), `HelperScript` (the PowerShell swap script: paths as single-quoted literals, UTF-8 with BOM), `UpdateService` behind `IUpdateSource` + `IHelperLauncher`), `UpdateWindow` (the dialog: notes, Update now with progress + Cancel, Later, Skip this version), `UpdateCoordinator.cs` (check on open: dialog or tray balloon; Settings' Check now; start the helper, then `Quit(ask: false)`), `App.Update.cs` (wiring, `--update-now`), `App.SmokeTest.Update.cs` |
| Tests | `tests/EasyVocal.Tests/` (+ `Helpers/FakeAudio.cs`, `Signals.cs`, `MiniFlac.cs` = a tiny FLAC writer, Windows has no FLAC encoder; `Speech.cs` = real speech made once per run with Windows' TTS through PowerShell (David/Zira/Maria when installed, a synthetic vowel otherwise); `VoiceFeatures.cs` = what a voice sounds like in numbers; `WavFile.cs`); `VoiceCharacterTests.cs` measures every built-in voice on that speech; some plain app classes are linked in via the csproj; `E2ETests.cs` runs the built app with `--e2e` |
| Tools | `tools/Make-Icon.ps1` (draws `Assets/EasyVocal.ico`), `tools/Publish-Release.ps1` |

## Global constraints

- Windows 10/11 only, `net8.0-windows`; self-contained win-x64 release, download under ~80 MB.
- Free, MIT, no ads, nothing locked. Public repo: no secrets, no personal paths.
- No real celebrity/character voices and no recognisable meme clips shipped; starter sounds are generated by code.
- Added latency under ~50 ms with classic filters (DSP <= 30 ms; WASAPI buffers <= 20 ms).
- OFF or the "Normal" voice = the clean voice still reaches the virtual mic.
- The voice only reaches the call while the app runs; no background service.
- UI text in English through `L.T("...")`; `pt-BR` ships complete. Dark theme by default; text contrast >= 4.5:1.
- Custom filters: 9 sliders with friendly names (Pitch, Voice body, Robot, Echo, Old radio, Distortion, Whisper, Growl,
  Big room), no units, no dB/Hz. The built-in voices also use `VoiceSliders` fields the editor doesn't show (Octave,
  Bass, Treble, Wobble, Monotone, Vocoder); a copy of a built-in keeps them. Every new field defaults to neutral (0),
  so old settings files load unchanged.
- **Built-in voices must sound different.** We can't listen, so `VoiceCharacterTests` measures each voice on real
  speech (pitch and its spread, voiced share, centroid, tilt, flatness, roughness, room tail, octave-below energy,
  nonlinearity, loudness): every pair at least 1.5 "audible steps" apart, plus Demon = octave layer, Monster = rough,
  Giant = longest tail and clean, Female up / Male down, the two robots different, loudness within -6..+5 dB. Retune
  a voice with that table (it is printed in the test output) and render the listening samples for the owner.
- Dependencies: only small permissive packages (NAudio, NAudio.Vorbis; xUnit in tests).
- Audio thread: no allocation per block, no locks held while doing DSP, no LINQ.
- VB-CABLE is not bundled (donationware); the app downloads the official installer and verifies its signature.

## Hard rules

- **Persisted ids are never renamed** (voice ids like `"movie-robot"`, sound ids, settings keys, general hotkey keys
  `toggleFilter`/`stopSounds`/`muteMic`). Unknown or missing settings keys get defaults.
- **Versioning X.Y.Z** lives in `Directory.Build.props` (`<Version>`). Every version is a tag `vX.Y.Z` and a GitHub
  release made by `tools/Publish-Release.ps1`; the asset name `EasyVocal-win-x64.zip` is fixed (README link).
- **Encoding**: sources are UTF-8 without BOM and contain emoji. Never rewrite files with PowerShell
  `Get-Content | Set-Content` / `-replace` (it double-encodes). Use an edit tool or `sed`. After bulk edits grep for
  mojibake: `â€|Ã©|ðŸ|ï¼`.
- Every user-visible text goes through `L.T(...)`; add the pt-BR entry in `Lang/pt-BR.json`.
- **A sound's original file is never modified.** The sound editor saves a new WAV in `Sounds\` ("<original>
  (edited).wav"), points `SoundItem.FileName` at it and keeps the original in `SoundItem.OriginalFileName` ("Back to
  original"). Never delete a file another sound still uses (`ItemOps.IsFileUsed`).
- **Windows fit small screens** (1366×768 at 125 % is ~580 px tall): every window goes through `WindowPlacer.Use`, is
  resizable where it makes sense, has MinWidth/MinHeight, and keeps long content in a ScrollViewer with its
  Save/Close buttons outside it. The smoke test renders each one at its minimum size (`smoke_small_*.png`,
  `smoke_audio_editor_small.png`) and checks those buttons are on screen.
- **The updater never runs in the tests and never installs from a source build.** Smoke and e2e build no
  `UpdateCoordinator` (the smoke test uses a fake `IUpdateSource` and `IHelperLauncher`); a build under the repo
  (bin\Debug, publish\win-x64) is a source build. The helper swaps only `EasyVocal.exe` (a `.old` copy until the new one
  is in place, put back if the copy fails), waits up to 60 s for EasyVocal to close, then opens it again; `%APPDATA%`
  is never touched. Settings keys: `CheckForUpdates`, `SkippedUpdateVersion`.
- **Never push, tag or publish a release without the owner's word.**
