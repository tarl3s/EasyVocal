# AGENTS.md — EasyVocal

Context for AI coding assistants. `README.md` (English) and `README.pt-BR.md` are the player-facing description;
`docs/superpowers/specs/` holds the design and `docs/superpowers/plans/` the implementation plan.

## The project in one paragraph

EasyVocal is a **free, light real-time voice changer + soundboard for any call app** (C# / Avalonia / .NET 8) for
Windows, macOS and Linux. Microphone → filter → mixer (+ soundboard) → a virtual cable the call app picks as its
microphone: VB-CABLE on Windows, BlackHole 2ch on macOS, a PipeWire/PulseAudio device EasyVocal makes itself on Linux.
Audience: people who want fun on calls, not audio people (buttons, emojis, plain sliders). MIT, public repo.
Owner speaks Brazilian Portuguese; code, comments, UI source text and commits are English.

**1.0.0 is going cross-platform** (Windows + macOS + Linux; design in
`docs/superpowers/specs/2026-10-25-cross-platform-design.md`). Phase 1 is done: `EasyVocal.Core` is portable `net8.0`
and everything Windows-only sits behind its `Core/Platform` interfaces in `EasyVocal.Platform.Windows`. Phase 2 is
done: the UI is Avalonia 11 (one UI for every OS, WPF and WinForms are gone) and the smoke and e2e tests also run
headless. Phase 3 is done: `EasyVocal.Platform.MacOS` and `EasyVocal.Platform.Linux` (sharing `EasyVocal.Platform.Unix`),
wired in `PlatformFactory.cs`. Phase 4 is done: `tools/Publish-Release.ps1` builds all four downloads from Windows
(`tools/EasyVocal.Packager` makes the .app zips and the tar.gz) and the READMEs have a button per OS.
**CI** (`.github/workflows/ci.yml`) builds and tests on Windows, macOS and Linux runners on every push and
every pull request; a `packages` job builds the four downloads exactly as `tools/Publish-Release.ps1` does and
`tools/Test-Package.sh` unpacks them on real macOS and Linux runners (tar / ditto, exec bits, `ldd`, `install.sh`, the
README's ad-hoc codesign) and runs the unpacked program's `--smoketest` and `--e2e`. An `ai-pack` job (Windows, macOS arm64,
Linux) installs the real AI pack from the `ai-pack-v1` release with the app's own downloader (`tools/EasyVocal.AiPack install`),
runs the real-model tests on it (`EV_AI_PACK`, its own ONNX Runtime) and the packaged download's `--e2e` with
`--e2e-ai-pack` (`tools/Test-Package.sh` / `tools/Test-WindowsPackage.ps1`): the release app has no ONNX Runtime of its
own, so this is where the pack's runtime is proven. **Nobody has used the app on a real Mac or Linux desktop yet** (CI runs the tests, not a person),
so the READMEs call those builds "Preview".

**2.0.0 adds AI voices** (design `docs/superpowers/specs/2026-10-25-ai-voices-design.md`; phase 1 research on the
`spike/ai-voices` branch: `docs/superpowers/research/2026-10-25-ai-voices-phase1.md`). Phase 2 (the engine, no UI) is
on `feat/ai-voices`: `src/EasyVocal.Ai` runs RVC v2 voices through ONNX Runtime, the voice chain has a converter slot
for them, `AppController` loads the active AI voice. Phase 3 (the UI and the pack's lifecycle) is done on the same branch:
the Voices tab's AI section, Add AI voice, Settings' AI section, the pack's download/install/remove, credits. Phase 4 is done on the same branch: the per-OS AI pack assets (`tools/EasyVocal.AiPack pack`, published as the release
`ai-pack-v1`, never the repo's "Latest"), RMVPE stored as fp16, DirectML's adapter choice, the bundled voices re-picked as
3-way blends, the `ai-pack` CI job, the READMEs' AI section; version 2.0.0. Left: the 2.0.0 gate (review, release).

## Project layout

| Project | Target | What |
|---|---|---|
| `src/EasyVocal.Core` | `net8.0` (portable) | model, storage, DSP, engine, hotkey logic, localizer, audio editing, decoders, the updater (rules, unpacking, service), the platform interfaces (`Core/Platform/PlatformInterfaces.cs`), `AppLog` |
| `src/EasyVocal.Platform.Windows` | `net8.0` marked `SupportedOSPlatform("windows")`, no UI | WASAPI devices + Windows' default devices, Media Foundation / ACM decoders and the MP3 encoder, `RegisterHotKey` (on its own message-only window), the Run key, the Start menu `.lnk`, VB-CABLE installer + Authenticode, the updater's PowerShell swap; `WindowsPlatform.Services` hands them out |
| `src/EasyVocal.Platform.Unix` | `net8.0` marked macOS + Linux | what both share: PortAudio devices (own P/Invoke, `PortAudio/`; natives from the `org.k2fsa.portaudio.runtime.*` packages, Apache-2.0; 48 kHz mono float, 10 ms blocks, any device rate converted both ways; the list from an optional `IDeviceSource`, PortAudio started again only when the devices changed — never on a timer — and a change under open streams fails them so the engine reopens; same-name devices matched in order; a 2 s poll for stalled streams); a closing stream (Dispose never waits, not even for the backend's lock) answers paComplete with silence and PortAudio stops it on its own audio thread; a thread of its own waits for PortAudio's stream-finished callback, then Pa_AbortStream (nothing left to stop) so it is "stopped" before Pa_CloseStream (which would abort it otherwise) — never a stop from outside while the audio thread may be stopping it (both end in AudioOutputUnitStop: macOS froze); only a stream with no callback for 1 s is aborted from outside, its callback answering paContinue from then on; opens wait for closes (`PortAudioStream`, tested on pretend calls in `PortAudioCloseTests`), `ICommandRunner` (pactl, brew, open… in `C.UTF-8` on Linux, `StreamAsync` for `pactl subscribe`; tests fake it), `Opener`, `InstallLayout` (the release's `easyvocal-files.txt` marker, App Translocation), the updater's sh helper (`ShHelperScript`, `ShLauncher` with setsid) and `UnixUpdaterPlatform` (.app / folder) |
| `src/EasyVocal.Platform.MacOS` | `net8.0` marked `macos` | `CoreAudioDevices` (the list: UIDs, names, defaults), `MicPermission` (AVFoundation's status via the ObjC runtime; refused → `MicPermissionDeniedException`, also while recording), `BlackHoleProvider` (`brew install blackhole-2ch` in the background with an osascript password dialog through `SUDO_ASKPASS`, else the official page), `CarbonHotkeys` + `MacKeys` (kVK codes; Win = ⌘) (options = kEventHotKeyExclusive = 1 << 0; another bit is paramErr for every key), `LaunchAgent` (`~/Library/LaunchAgents/com.tarl3s.easyvocal.plist`, `--tray`), `NoAppMenuEntry`; `MacPlatform.Services` |
| `src/EasyVocal.Platform.Linux` | `net8.0` marked `linux` | `LinuxAudio` (picks without waiting: `PulseBackend` when pactl and libpulse-simple are there, PortAudio when its first look finds no server, back to PulseAudio once `pactl info` answers again), `PulseBackend` (the UI reads only a snapshot; pactl — JSON where it can — runs on a worker woken by one long-running `pactl subscribe`, a slow poll only while that is down; a failed look keeps the last good snapshot; streams whose device left the snapshot fail, unknown ids and EasyVocal's own mic are refused; libpulse-simple streams by name, ~30 ms queued), `PactlVirtualMic` (module-null-sink `easyvocal_out` + module-remap-source `easyvocal_mic`, unloaded on quit, leftovers (and halves of a failed try) unloaded by name; a default the system moved to them is put back), `X11Hotkeys` + `X11Keys` (XGrabKey on its own thread and display, poll() + wake pipe, MappingNotify re-grabs, Caps/Num Lock variants, none on Wayland: `DesktopSession`), `AutostartEntry` (`~/.config/autostart/easyvocal.desktop`), `AppMenuEntry` (`~/.local/share/applications` + icon in its size's hicolor folder), `TrayHost` (StatusNotifierWatcher on D-Bus), `AlsaDevices`; `LinuxPlatform.Services` |
| `src/EasyVocal.Ai` | `net8.0` (portable) | the AI voices' engine (RVC v2 on ONNX Runtime; see "AI voices" below). References only `Microsoft.ML.OnnxRuntime.Managed` 1.23.2; the native library comes from `OnnxRuntime.Native.props` (DirectML 1.23.0 on Windows, the CPU package 1.23.2 elsewhere — 1.23.x is the last with osx-x64) in the tests, the tool and Debug builds of the app, and later from the AI pack (`<data>/AI/runtime/`, `OrtRuntime.UseNativeFolder`). Embeds the weight-less templates (`Templates/*.onnx.gz`) and the bundled voices' recipe (`Voices/bundled-voices.json`) |
| `src/EasyVocal` | `net8.0`, Avalonia 11 (exe `EasyVocal(.exe)`) | the app: windows, `AppController` (talks only to the interfaces and an `IUiDispatcher`), tray, smoke/e2e test modes; `PlatformFactory.ForThisComputer` is the one place that picks the platform (Windows, macOS, Linux; anything else a no-devices stand-in). It references every platform project and calls each only behind `OperatingSystem.IsWindows()/IsMacOS()/IsLinux()` (CA1416 checks) |
| `tests/EasyVocal.Tests` | `net8.0` | everything portable (runs on any OS), the updater with a pretend platform (`Helpers/FakeUpdate.cs`); fixtures in `Fixtures/` (a 440 Hz tone as MP3 and OGG) |
| `tests/EasyVocal.Ai.Tests` | `net8.0` (portable) | the AI engine on any OS: `.pth`/ONNX/`.index` reading against PyTorch and faiss (tiny fixtures in `Fixtures/`, made by `tools/ai/export/make_fixtures.py`), the audio-thread converter with stand-in workers (`StreamingTests`), the whole engine on a tiny pack (`ServiceTests`), imports by file and by link (a local HTTP server, `Helpers/LocalHttp.cs`), and with the real models (`RealModelTests`, `PipelineTests`: bundled voices, latency per level through the real `AudioEngine`). The real models are RVC's MIT files, downloaded once into `EV_AI_MODELS` (default `%TEMP%\EV_AiModelCache`, kept; CI caches `upstream/`) and turned into a pack there; "SKIPPED: …" when they can't be had (`EV_AI_OFFLINE=1`). Latency budgets are reported (`OVER BUDGET`), enforced only with `EV_AI_STRICT=1` (an idle PC: the suites run in parallel); on CI (`CI=true`) the quality checks of a level the runner can't keep up with are skipped |
| `tests/EasyVocal.FfmpegStandIn` | `net8.0` exe | a tiny program the decoder tests run in place of ffmpeg (what it does is written in the "sound file" it gets) |
| `tests/EasyVocal.Tests.Windows` | `net8.0-windows` | registry, `.lnk`, VB-CABLE/Authenticode, Media Foundation / ACM, hotkeys, the PowerShell helper, the e2e run; links the portable tests' `Helpers/` |
| `tests/EasyVocal.UiTests` | `net8.0` (portable) | Avalonia.Headless (drawn with Skia): the app's own smoke and e2e steps in-process on pretend devices/hotkeys/startup/virtual mic (`E2E/TestPlatform.cs`), the e2e relaunch as a second session; `AppController` on a hand-pumped `IUiDispatcher`; `KeyMap`. `EV_UI_SNAPSHOTS=<folder>` keeps the PNGs |
| `tests/EasyVocal.Tests.Platforms` | `net8.0` (portable, CA1416 off) | the macOS/Linux projects on any OS with a fake `ICommandRunner` (`FakeRunner.cs`): key maps, plist and .desktop files, pactl's arguments and output, BlackHole's install, the sh helper's text, the updater's names and unpacking; PortAudio for real (its win-x64 native is referenced too). Real-system tests check the OS: CoreAudio/Carbon on macOS, Xvfb grabs on Linux, the sh helper run with `/bin/sh`, and the virtual mic carrying a tone through a real PulseAudio server only with `EV_PULSE_TESTS=1` (CI sets it), and `LinuxVoicePathTests`: every built-in voice through real PulseAudio (a null sink as the mic, the real `AudioEngine`, a null sink recorded) against its offline render, `VoiceFeatures.Distance` < 0.6 and no dropouts; `ReleasePackagingTests` runs `tools/EasyVocal.Packager` on pretend publish folders (Info.plist, .icns, zip/tar modes, `easyvocal-files.txt`, no links, the updaters unpack them, install.sh run with `/bin/sh` off Windows) |

Platform interfaces (`EasyVocal.Core.Platform`), bundled as `PlatformServices` (the tests swap pieces with `with { … }`):
- `IAudioBackend`: list/open devices, defaults (one default = both roles off Windows), set default where supported,
  `Changed`; `OpenMic` throws `MicPermissionDeniedException` when the system refuses the microphone.
- `IGlobalHotkeys` (`Available` + `UnavailableReason` for Wayland, `Check` = Free/Taken/Unknown, `Pressed` on any
  thread: `AppController` posts it to the UI) and `IHotkeyTestHooks` (`HighestId`, `SimulatePress`, tests only);
  `PlatformServices.CreateHotkeys()` (on the UI thread).
- `IStartupService`, `IShortcutService` (`Supported`, `Refresh` → bool, logs).
- `IVirtualMicProvider`: `Find`, `Cable` (`CableNames`: the device names `DeviceRules`, `DefaultDeviceRules` and `MicPicker`
  use; none of the cable's devices is ever the mic or the headphones), `CallAppDeviceName`, `Texts` (the setup's and settings' sentences, written with
  `UiText.Mark` so the translation test finds them), `CanInstall`/`InstallAsync`, `TakesDefaultsOnInstall` (gates the
  remember / put back / Fix logic), `CanCreate` + `CreateAsync`/`RemoveAsync` (called by
  `AppController` off the UI thread: at start, again from the 2 s recovery when its device went missing (10 s, doubled
  after each failure); at quit after saving, waiting 1.5 s at most — removed even when its creation failed or finished
  late; Linux finds its modules by name, and a creation that fails half-way undoes itself). Test runs get a read-only
  wrapper (`PlatformFactory.ForTests`; the e2e run the VB-CABLE look-alike), `OpenPage`.
  Windows = `VbCableProvider`, macOS = `BlackHoleProvider`, Linux = `PactlVirtualMic`.
- `IUpdaterPlatform`: `AssetNames(arch)`, `ExeName`, `InstallRoot` (what is replaced: the exe, the .app, the folder),
  `CanSelfUpdate` (else the release page opens), `Unpack` (zip or tar.gz through `UpdatePackage`'s shared checks),
  the helper (`HelperFileName`, `HelperEncoding`, `BuildHelper(HelperPlan Target/NewPayload)`, `Launcher`),
  `RelaunchArguments`.
- `Decoders` (the order `SoundLoader` tries, `Core/Sounds/ISoundDecoder`) and `Mp3Encoder` (`IMp3Encoder`, null where
  there is none). Nothing is set in a global: windows load with `controller.Platform.Decoders`.
- Init properties of `PlatformServices`: `Texts` (`PlatformTexts`, `Core/Platform/PlatformTexts.cs`: every sentence that
  names the OS — "Start with Windows" / "Open at login" / "Start at login", "Windows default" / "System default",
  "Restart your PC…", the mic-refused banner; Windows/MacOS/Linux sets), `OpenMicSettings` (the mic-refused banner's
  "Open settings"; null = no button) and `HasTray` (null = assume one; Linux asks D-Bus, off the UI thread, the window shown meanwhile unless `--tray`: without one, closing minimises).

## Build, run, test

```bash
dotnet build                       # 0 errors, 0 warnings expected
dotnet test                        # every test project (xUnit), including the E2E tests below (builds the app first)
dotnet test --filter Category!=E2E # everything but the real-window E2E test
dotnet test tests/EasyVocal.Tests  # only the portable tests (on macOS/Linux run these, tests/EasyVocal.Tests.Platforms and tests/EasyVocal.UiTests)
EV_PULSE_TESTS=1 xvfb-run -a dotnet test tests/EasyVocal.Tests.Platforms   # Linux: also the real PulseAudio virtual mic and X11 grabs (what CI does)
EV_UI_SNAPSHOTS=<folder> dotnet test tests/EasyVocal.UiTests   # headless smoke + e2e; keeps the smoke PNGs in <folder>
dotnet run --project src/EasyVocal
dotnet run --project src/EasyVocal -- --data <folder> --smoketest   # opens every window, renders PNGs, exits 0
dotnet run --project src/EasyVocal -- --data <folder> --e2e         # end-to-end test on test audio devices, exits 0
tools/Publish-Release.ps1 -NoUpload   # self-contained single-file zip -> publish\EasyVocal-win-x64.zip (< 80 MB)
EV_VOICE_SAMPLES=<folder> dotnet test --filter RenderListeningSamples   # every built-in voice on real speech, as WAVs + README.txt (voice-samples/ is git-ignored)
dotnet test tests/EasyVocal.Ai.Tests   # the AI engine (the real models: downloaded once into EV_AI_MODELS, see below)
EV_AI_SAMPLES=<folder> dotnet test tests/EasyVocal.Ai.Tests --filter Bundled_voices   # every bundled AI voice on real speech, as WAVs
dotnet run --project tools/EasyVocal.AiPack -- build --data <folder> --int8 <hubert_int8.onnx>   # an AI pack in <folder>/AI from RVC's public files (then bench / list / render)
dotnet run --project tools/EasyVocal.AiPack -- pack --data <folder> --out <dir>   # the release assets EasyVocal-AI-<os>.zip + .sha256 (all four OSes)
dotnet run --project tools/EasyVocal.AiPack -- install --data <scratch> [--keep <dir>]   # the real install from the ai-pack-v1 release, as the app does it
EV_AI_PACK=<scratch> dotnet test tests/EasyVocal.Ai.Tests --filter "FullyQualifiedName~PipelineTests|FullyQualifiedName~RealModelTests"   # the real-model tests on an installed pack (its own runtime)
dotnet run --project src/EasyVocal -- --data <folder> --e2e --e2e-ai-pack <EasyVocal-AI-*.zip>   # the e2e run with its AI step on a real pack (served locally like the release)
```

App arguments (`Program.cs` and `AppSession.cs`, parsed by `AppArgs.cs`): `--data <folder>` (use that folder instead of
`%APPDATA%\EasyVocal`), `--smoketest` (headless-ish UI check, PNGs and `easyvocal.log` go to the data folder),
`--e2e` (see below; `--e2e-ai <tests/EasyVocal.Ai.Tests/Fixtures>` adds the AI step on the tiny models,
`--e2e-ai-pack <EasyVocal-AI-*.zip>` on a real pack: `E2E/E2EAiPack.cs` serves either like the release on 127.0.0.1), `--tray` (start hidden in the tray), `--update-check` (a source build looks for a new release
anyway; it still never installs), `--update-now` (no window: download and install the latest release without asking,
log in `<data>\easyvocal.log` and `<data>\update.log`; exit 0 updated or already latest, 1 failed, 3 source build, 4
EasyVocal is running). The tests refuse to run without `--data` (exit code 2),
skip the first-run setup, never touch the real "Start with Windows" entry and don't hand over to (or wake) a running
EasyVocal. Tests create temp folders under `%TEMP%\EV_*` and must delete them.

**E2E test** (`AppSession.E2E.cs`, devices in `E2E/`): the real app (controller, engine, voice chain, mixer, windows) with
`TestAudioDevices` (an `IAudioBackend`) swapped into `AppController`'s `PlatformServices`: a microphone that plays a 150 Hz
vowel with pauses in real time on its own thread, and headphones and VB-CABLE that record what they get. It clicks
the real cards and buttons, opens the real dialogs, and checks the recorded audio (exact copy of the voice where the
voice should be clean, pitch, energy on the harmonics, sounds over the voice): no VB-CABLE then installed, Normal,
the voice cards, Settings and Setup listing the test devices, ON/OFF and mute, sound cards and Stop all, Hear me (and headphones whose stream stops coming back), a hotkey set through its dialog and pressed
(a real WM_HOTKEY in the real app; in-memory hotkeys headless), the filter editor (live preview, Cancel, hotkeys back), a custom sound added and deleted, the audio editor (0.5× then 3 up stays 0.5×, 1× keeps the pitch, Undo brings both back, saved), 🎭 Sounds use the voice (a sound takes the voice and follows a voice change), the
mic unplugged and back, VB-CABLE taking Windows' default devices (the banner's Fix, the setup's Install with a fake installer putting back what was there), then quit the normal way and a second run (`--e2e-relaunch`) checks that everything came
back — except the active voice: every start begins on Normal (`AppController`'s constructor), so nobody joins a call
with the last filter on. PASS/FAIL per step in `%TEMP%\easyvocal_e2e.log` and `<data>\e2e.log`; exit 0 only when every step passed.
`EasyVocal.Tests.Windows/E2ETests.cs` (trait `Category=E2E`) runs the built exe this way (it passes with a "SKIPPED" note
when the exe isn't built). It shows its windows for ~35 s and holds Ctrl+1 (or a free fallback) meanwhile.
`EasyVocal.UiTests` runs the same steps headless in-process (no windows on screen, nothing registered with the system;
the relaunch is a second `AppSession` on the same folder).

## Architecture map

| Area | Files |
|---|---|
| DSP (no UI, no NAudio device code) | `src/EasyVocal.Core/Dsp/`: `Effects.cs` (echo, radio, distortion, robot, whisper), `VoiceEffects.cs` (growl, "Big room" reverb, bass/treble tone, vocoder), `VoiceShifter.cs` (pitch/formant TD-PSOLA, plus wobble, monotone and a voiced-only mode for the octave layer), `Biquad.cs` (incl. shelves), `Limiter.cs`, `VoiceChain.cs` (`VoiceSliders` → a table of parameters → shifter [+ octave layer] → growl → vocoder → robot → distortion → radio → whisper → echo → room → tone; slides/crossfades; `CreateFollower` = another signal through the same voice), `IAudioEffect.cs` (+ `IAmountEffect`), `AudioFormat.cs` |
| Engine | `Core/Engine/`: `AudioEngine.cs` (mic → chain → mixer → outputs; with `SoundsUseVoice` the mixer goes through a follower of the chain first, so mute / Hear me / Hear sounds still tell voice and sounds apart), `AudioInterfaces.cs` (`DeviceInfo`, `IAudioInput`, `IAudioOutput`), `DeviceRules.cs` (`CableNames`: VB-CABLE, BlackHole, the Linux devices; finds the platform's cable, picks real devices), `DefaultDeviceRules.cs` (VB-CABLE taking Windows' default devices: remember, put back, Fix), `FormatConverter.cs`, `MicMeter.cs` (the setup's level bar), `SoundPreview.cs` (the sound editor's ▶ on the headphones) |
| Platform interfaces | `Core/Platform/PlatformInterfaces.cs`: `IAudioBackend` (the seam the controller opens devices through and reads/sets the system's defaults with; the e2e test passes its own) + `MicPermissionDeniedException`, `IGlobalHotkeys` + `HotkeyCheck` + `IHotkeyTestHooks`, `IStartupService`, `IShortcutService`, `IVirtualMicProvider` (+ `VirtualMicTexts`, `UiText`, `InstallStatus`/`InstallResult`), `IUpdaterPlatform`, `PlatformServices` |
| Sounds | `Core/Sounds/`: `SoundLoader.cs` (`Load`/`LoadWith(path, decoders, ct)` try each decoder that takes the extension; a failure, an empty result or "too long" moves on; only when none gave a sound: the first friendly answer (empty, too long, took too long), else "damaged"; no decoder for the extension = "install ffmpeg"; `Load(path)` alone = the portable list; `Extensions`/`DialogPattern` feed the file pickers), `SoundDecoders.cs` (`ISoundDecoder` with a `CancellationToken`, the portable list WAV/AIFF (NAudio.Core, also WAVE_FORMAT_EXTENSIBLE: 24-bit, float, 5.1) → OGG (NVorbis) → MP3 (NLayer) → ffmpeg, `SampleReading.ToMono48k` = downmix + resample + the 10-minute limit + cancellation), `FfmpegDecoder.cs` (`ffmpeg` on PATH or Homebrew's folders, Snap's last, looked for again until found; `-i file:<path>`, first sound track to mono 48 kHz f32 on stdout straight into the array; one 60 s deadline, killed on timeout or cancel; a non-zero exit is a failure; never installed), `SoundWriter.cs` (16-bit WAV; `IMp3Encoder` for MP3), `AudioEdit.cs` (the sound editor's pure operations on mono 48 kHz: cut/trim/split, speed with WSOLA time-stretch or resample, pitch, gain/normalize, fades, reverse, echo, a voice run offline through `VoiceChain` with its latency removed and its tail kept (on a part it rings on over what follows, like the echo), `Retune` = speed → pitch → voice of a part in one go, remove silence), `AudioHistory.cs` (its undo/redo, 20 steps, ~256 MB cap), `SoundMixer.cs`, `StarterSoundSynth.cs` (the 10 starter sounds are generated by code), `LoadTickets.cs` (drops a background decode of a sound that changed meanwhile), `WavePeaks.cs` (min/max of any range from a pyramid, for the editor's wave) |
| Model and storage | `Core/Model/`: `AppSettings.cs`, `BuiltIns.cs` (14 voices, each with its own signature, see below; starter sounds), `ItemOps.cs` (`Move`: a card's place in the Voices / Sounds list, which `SettingsStore` keeps in the file's order (a built-in new in a version goes after the built-in before it); `ImportSound`: a picked file is copied into `Sounds`, a video only as a WAV of its sound; also the edited-copy rules: `WriteSoundFile`, `UseEditedFile`, `BackToOriginal`, `AddSplitSounds`; a file stays while any sound uses it as `FileName` or `OriginalFileName`), `Core/Layout/WindowFit.cs` (`WindowBox`, clamp a window into a screen's working area, pick the screen, does a remembered place still fit); `Core/Storage/SettingsStore.cs` (`settings.json` + `Sounds\` in `%APPDATA%\EasyVocal`, `~/Library/Application Support/EasyVocal` on macOS, `~/.config/EasyVocal` on Linux), `PathCompare.cs` (`PathCompare`: case-sensitive on Linux only, also for the Sounds folder's file names; `FileNames.IsPlain`: a plain file name by this OS's rules — Windows' invalid characters, only `/` and NUL elsewhere — never `.` or `..`), `TempFolders.cs` (`Cleanup.TryDelete`: the one never-throwing delete of a file or folder; stale attempt folders); `Core/Logging/AppLog.cs` |
| Hotkeys and text | `Core/Hotkeys/` (`Hotkey.cs` (Windows virtual-key codes), `HotkeyMap.cs`), `Core/Text/Localizer.cs` |
| Windows platform | `src/EasyVocal.Platform.Windows/`: `WindowsPlatform.cs` (`Services`; `Decoders()` = WAV/AIFF → ACM WAV (compressed WAVs, as 0.2) → OGG → Media Foundation (MP3 and the rest, as 0.2's AudioFileReader did) → ACM MP3 → NLayer → ffmpeg), `Audio/` (`WindowsAudioBackend` (`Instance`; `ReadOnlyDefaults` for the smoke test), `DeviceList`, WASAPI mic (E_ACCESSDENIED → `MicPermissionDeniedException`) and speaker, `DefaultDevices.cs` = IPolicyConfig, never called by the tests: the smoke test gets read-only defaults and no installer), `Sounds/MediaFoundationCodecs.cs` (MF, ACM WAV, ACM MP3, MP3 encoder), `Hotkeys/WindowsGlobalHotkeys.cs` (RegisterHotKey on its own message-only window, created on the UI thread whose loop delivers WM_HOTKEY), `Shell/` (`StartupRegistration` = Run key, `StartMenuShortcut` = .lnk), `VirtualMic/` (`VbCableProvider` incl. its setup texts, `VbCableInstaller`, `Authenticode`), `Update/WindowsUpdater.cs` (`HelperScript`, `PowerShellLauncher`, `WindowsUpdaterPlatform`: unpack only EasyVocal.exe, "MZ" check, folder probe) |
| App (Avalonia) | `src/EasyVocal/`: `Program.cs` (entry: the `--data` rule, `--update-now`, `SingleInstance.cs` = named mutex + event on Windows, a lock file + a Unix socket of its own elsewhere (at the path .NET's pipe clients connect to; .NET's shared pipe server lost a wake now and then on macOS), closed at once (with the connections being read) before the lock is let go, its file removed only while a `.owner` token next to it is still ours: a held lock waits for the socket to appear, one that is there is never taken over; then Avalonia with an explicit shutdown), `App.axaml(.cs)` (theme; `Fonts()` = Windows' fallbacks: symbols from Segoe UI Symbol, emoji from Segoe UI Emoji), `AppSession.cs` (one run: settings, language, controller, main window, tray, quit; a test host can start a second one), `PlatformFactory.cs` (picks the platform; `ForTests` = what the smoke and e2e runs use instead), `AppController.cs` (wires engine, settings, hotkeys, the virtual mic's lifecycle; only sees the platform interfaces and `IUiDispatcher` = `UiDispatcher.cs`, Avalonia's dispatcher in the app, a hand-pumped one in the tests), `AppWindow.cs` (every window's base: icon, `Place` → `WindowPlacer.cs`, `ShowAsync` = a dialog over its owner or on its own from the tray, `Accepted`, the open-window list the tests use), `MainWindow` (+ `MainWindow.Reorder.cs`: drag a card onto another, a bar shows where, a drop off every card moves nothing; "Move left / right" in its menu; what is shown, in order, is `ItemOps.VisibleVoices`/`VisibleSounds`, also for the tray menu), `SettingsWindow`, `SetupWindow` (first run, the virtual mic's install; its texts come from the provider; the level bar shows the audio's level when the audio has, or reopens by itself, that mic (`AppController.AudioMicId`), asked again on every change, and never opens a second stream on it), `FilterEditorWindow`, `SoundEditorWindow`, `AudioEditorWindow` ("✂ Edit audio…": wave, selection, headphone playback with playhead, the `AudioEdit` tools, speed (+ its mode), pitch and voice = three settings of the picked part, all made again from the part before the first of them on every change (`Tune`/`TuneBase` kept per `AudioState`, rendered by `AudioEdit.Retune`: speed → pitch → voice; 1× / 0 / Normal give each back; the lists and the "Now: +3" pitch show the part's settings, also after Undo / Redo, which pick the part again; another part or any other edit bakes them and they show 1× / 0 / Normal; pitch two octaves either way at most, Higher / Lower turn off before that), a picked part kept when the mouse leaves the wave (Esc drops it), Save / Back to original / Save a copy as…) + `WaveformView.cs` (the wave control: min/max columns read from `Core/Sounds/WavePeaks.cs`, a min/max pyramid built once per sound, ruler, zoom, drag-select), `WindowPlacer.cs` (never bigger than or off the screen, place remembered in `AppSettings.WindowPlacements` in primary-screen DIPs), `HotkeyCaptureDialog` + `KeyMap.cs` (Avalonia keys → the saved virtual-key codes), `AskDialog.cs`, `Tray.cs` (Avalonia `TrayIcon` + native menu) + `TrayNotice.cs` (the corner notice the tray's messages use), `Loc.cs` (`{loc:T '…'}`) + `Lang/pt-BR.json`, `Theme.axaml` (palette + control themes; Fluent underneath for TextBox and menus), `Res.cs`, `AppSession.SmokeTest.cs` + `.Editor.cs` (the audio editor's tools, every window at its smallest size) + `.Update.cs`, `AppSession.E2E.cs` + `E2E/` (test devices, signal and measurements, `TestPlatform.cs` = in-memory hotkeys/startup and a pretend VB-CABLE), `Ui.cs` (the tests' clicks) |
| Updater | `src/EasyVocal.Core/Update/` (portable; `Updater.cs`: `AppVersion` (tags, semver order), `UpdateRules` (GitHub's release JSON, `PickAsset` from the platform's names, when to offer: newer, not draft/pre-release, not the skipped version unless "Check now"; `IsSourceBuild` = an `EasyVocal.sln` or `src\EasyVocal\EasyVocal.csproj` above the exe), `ReleaseNotes` (Markdown to plain lines); `UpdatePackage.cs` (`ExtractFile` one file from a zip, `ExtractAll` a zip or tar.gz; zip-slip, no links, size limit, Unix permission bits kept); `UpdateService.cs`: `UpdateService` behind `IUpdaterPlatform` + `IUpdateSource` + `IHelperLauncher`: the payload replaces the platform's `InstallRoot`); Windows' part in `Platform.Windows/Update/WindowsUpdater.cs` (`HelperScript` = the PowerShell swap script: paths as single-quoted literals, UTF-8 with BOM); the app's `UpdateWindow` (the dialog: notes, Update now with progress + Cancel, Later, Skip this version; opens the release page when the platform can't self-update), `UpdateCoordinator.cs` (check on open: dialog or tray notice; Settings' Check now; start the helper, then quit without asking), `AppSession.Update.cs` (wiring through `controller.Platform.Updater`, `--update-now`), `AppSession.SmokeTest.Update.cs` |
| Tests | `tests/EasyVocal.Tests/` (portable, `net8.0`: + `Helpers/FakeAudio.cs`, `FakeUpdate.cs` (a pretend updater platform, source and launcher), `Signals.cs`, `MiniFlac.cs` = a tiny FLAC writer, Windows has no FLAC encoder; `Speech.cs` = real speech made once per run with Windows' TTS through PowerShell (David/Zira/Maria when installed, a synthetic vowel otherwise, e.g. on macOS/Linux; `EV_SPEECH=synthetic` or `=en` reproduces those inputs (CI's
Windows has no Maria) — tune a voice until it passes on all three); `VoiceFeatures.cs` = what a voice sounds like in numbers; `WavFile.cs`); `VoiceCharacterTests.cs` measures every built-in voice on that speech; `DecoderTests.cs` (NLayer, NVorbis, extensible WAV, the loader's "keep looking", ffmpeg through the stand-in: output, non-zero exit, deadline + kill, cancel; a real ffmpeg only when installed, "SKIPPED" otherwise); `UpdateServiceTests.cs`; `MeterPreviewTests.cs` (MicMeter, SoundPreview, PathCompare, data folders); `AppArgs.cs` is linked in via the csproj. `tests/EasyVocal.Tests.Windows/` (`SetupTests` = Run key + VB-CABLE pack, `StartMenuShortcutTests`, `UpdaterTests` = Windows' names and unpack, PowerShell helper run for real; `WindowsSoundTests` = Media Foundation, ACM mu-law / IMA ADPCM, which decoder reads MP3; `WindowsHotkeysTests` = the message-only window; `E2ETests.cs` runs the built app with `--e2e`). `tests/EasyVocal.UiTests/` (`HeadlessAppTests` = the smoke and e2e steps headless, `ControllerTests` = the controller on a hand-pumped dispatcher, `KeyMap`; `TestAppBuilder.cs` = the real App on the headless platform with Skia) |
| Tools | `tools/Make-Icon.ps1` (draws `Assets/EasyVocal.ico`; `-PngDir` = the same drawing as 16-1024 px PNGs), `tools/Publish-Release.ps1` (publishes win-x64, osx-arm64, osx-x64, linux-x64 self-contained single file; Windows keeps the natives inside the exe, macOS/Linux next to it), `tools/EasyVocal.Packager` (plain .NET console: `Release.Pack` makes each download: `MacApp` = Info.plist (com.tarl3s.easyvocal, LSMinimumSystemVersion 11.0, NSMicrophoneUsageDescription), PkgInfo, `.icns` built from PNG chunks, the program and dylibs 0755; `LinuxApp` = `EasyVocal/` with easyvocal.png, easyvocal.desktop and install.sh; `Archives` = zips with Unix modes in the external attributes and "made by Unix", ustar tar.gz with modes, never links, no .pdb; `Manifest` = `easyvocal-files.txt` in each install root: next to EasyVocal.exe, in `EasyVocal.app/Contents/Resources/`, in `EasyVocal/`) |
| AI tools | `tools/EasyVocal.AiPack` (plain .NET console: `fetch` RVC's public files, `build` a pack, `voices` = write the bundled voices again from the recipe (the "generation script"), `list`, `bench`, `render` a recording through a voice offline or streamed, `pack` = the release assets (`PackRelease.cs`: engine + bundled voices + that OS's ONNX Runtime taken from the pinned NuGet packages (SHA-256 of each .nupkg in the code: `microsoft.ml.onnxruntime` 1.23.2 for macOS/Linux, `.directml` 1.23.0 + `microsoft.ai.directml` 1.15.4 for Windows) with their licences in `runtime/licenses/`, `engine/NOTICES.txt`, a `.sha256` per zip), `install` = the real download from the release (`PackInstaller` + `Downloader`, `--keep` the checked zip), `blends` = candidate bundled voices rendered offline for `judge_voices.py`); `tools/ai/export/*.py` (dev time only, a Python venv with torch/transformers/onnx and RVC's repository): `export_rvc.py templates` = the weight-less RVC v2 templates, `export_hubert.py template` = HuBERT's, `make_fixtures.py` = the tests' tiny models, `variants.py` = int8/fp16 (`hubert_int8.onnx`), `rel_attn_patch.py` = trace-friendly attention (same math), `judge_voices.py` = the bundled voices' pick (WavLM-base-plus-sv x-vectors + the MFCC print of `VoiceMeasure`: `survey` the 109 speakers, `candidates` 3-way blends, `judge` a table / `--pick` the least-alike set) |

## AI voices (`src/EasyVocal.Ai`)

- **Pipeline** (RVC v2, RVC's realtime scheme `rtrvc.py`, per stream `Rvc/RvcStream.cs`): mic 48 kHz → 16 kHz
  (`Dsp/PolyResampler.cs`) → rolling context (extra + crossfade + 10 ms search + block) → HuBERT base layer 12
  (`Rvc/ContentEncoder.cs`, 768 values per 20 ms, doubled to 100 fps, optional `.index` blend `Rvc/FeatureIndex.cs`)
  + pitch on the newest block + 50 ms (`Rvc/Pitch.cs`: RMVPE through ONNX, YIN in C# on Light; `F0.Post` fills gaps
  ≤ 150 ms, shifts, coarse bins; `AutoKey` moves the input to the voice's natural pitch `TargetHz` from the median of
  the last ~6 s of voicing) → generator (`Rvc/RvcGenerator.cs`: enc graph over all frames, dec graph = flow +
  NSF-HiFiGAN over the newest block + crossfade + search frames, speaker vector `g` an input; or one RVC WebUI /
  w-okada graph) → output gate from the input level → SOLA + sin² crossfade → 48 kHz. `Rvc/RvcOffline.cs` = a whole
  recording at once. Shapes are fixed per level, tensors are preallocated `OrtValue`s (no allocation per block).
- **Levels** (`AiQuality.cs`): Light = 200 ms blocks, 0.4 s context, YIN, int8 content encoder; Balanced = 200 ms
  (120 on a GPU), 1 s, RMVPE, index 0.5; Max = 250 ms (150 GPU), 2 s, RMVPE, fp32, index 0.75.
- **Audio thread** (`Engine/AiVoiceConverter.cs`, an `IVoiceConverter`): copies the mic into a ring and plays the
  worker's output ring `L` = block + cushion behind, by absolute sample position (latency = L + crossfade + search +
  the resamplers, reported by `LatencySamples`). **The person's own (dry) voice is never played while an AI voice is
  picked** (owner's ruling): a sample that isn't ready (a gap) repeats the last 20 ms of converted audio back and forth
  (continuous at each turn) fading over 100 ms into the *stand-in voice* (`AiFallback`: the input from the same moment
  shifted towards the voice's `TargetHz`, ±3..9 semitones, through a `VoiceShifter`), 5 ms crossfade back; counted as
  an under-run, the cushion grows 20 ms (and gives 10 ms back after 20 s without a gap). The stream's start, an
  overload (a worker more than 3 blocks behind skips ahead) and a failed worker (`Error`) play the stand-in voice too.
  `Ready` = converted audio was played. No lock, no allocation, no wait; the worker is woken on every block; Dispose
  from any thread (it then plays the stand-in voice, never the input). `WaitStopped` = its worker has left the model:
  `AiVoiceService.Dispose` disposes every converter it made and waits for them (3 s at most, else the models are left
  to the process's end) before it closes any model.
  The cushion starts at `AiLevels.CushionFor(p95)` = measured 95th percentile × 1.25 + 15 ms (`AiSettings.MeasuredBlockMs`
  from the benchmark), else the level's default.
- **Chain**: `VoiceChain.SetConverter` puts it in front of the classic effects (they play on the converted voice);
  in/out and OFF crossfade over 40 ms; a converter that wasn't fed until it fades in (after OFF) is `Reset` first.
  `VoiceChain.Warm(next)` feeds the next converter the input unheard until `SetConverter(next)` swaps it in.
  `VoiceChain.Retire(c)` disposes a converter once the chain no longer uses it (not set, not warming, the audio thread
  done fading it out: two free checks 50 ms apart; while the audio is stopped it waits), `RetireAllNow` after the
  engine is gone. Followers never get a converter.
  `AudioEngine` with a converter: mute applies before it, "Sounds use the voice" mixes the sounds into its one stream
  (the engine's sounds chain is a follower), and Hear me plays the voice as spoken unless `HearConvertedVoice`
  (Settings → "Hear me plays the AI voice itself"); Hear sounds still gets a copy of the sounds as they are (not on
  top of the AI voice itself in the headphones, which carries them).
- **Controller** (`AppController`, AI section): picking an AI card loads it off the UI thread while **the voice before
  it keeps playing** (`PlayingVoiceId`; the card shows a spinner, `AiLoadingVoiceId`) — unless that voice is clean
  (Normal, none yet, another AI voice's stand-in): then the voice's stand-in plays (`IVoiceConverterSource.StandIn`,
  `StandInVoice` = a `VoiceShifter` towards its `TargetHz`, ready at once) —, warms it in the chain and swaps
  it in (with its sliders) once `Ready`; an overtaken load is disposed; a failed or unavailable load gives the card back
  to the voice that plays and sets `AiProblem` (the banner, ✕ = `DismissAiProblem`); a worker that fails while
  playing (it plays its stand-in) is told and tried once more on the processor (`CreateAsync(.., cpuOnly)`), then
  `Failed`. The key reloaded on change = voice + its quality (or Settings') + device + benchmark time. The filter
  editor's preview only changes the sliders (an AI voice keeps playing, the preview's sliders after it; nothing
  reloads). Quit: save, stop the engine, retire every converter now, dispose the AI engine (which waits for the
  workers), then remove the virtual mic. `Ai` (`IAiVoices`, `AiVoices.cs`): `AiManager` = `AiVoiceService` +
  `PackInstaller` + `VoiceImporter` (normal runs), `StandInAi` (the smoke test: every state, no ONNX Runtime), the
  tiny test pack (`E2E/TinyAiPack.cs`, the e2e run with `--e2e-ai <fixtures>`: a local web server plays the release).
  At start a card per installed AI voice (`SyncPresets`; bundled cards stay `BuiltIn` and named by their pack;
  deleting an imported voice's last card deletes its files).
- **UI**: `MainWindow.Ai.cs` (the AI section: download card with progress/cancel/resume and the first PC test;
  AI cards with a 🧠 badge and spinner, reordered among themselves; `AI · N ms` in the top bar), `AddAiVoiceWindow`
  (file or link, name, emoji, "Only use voices you have the right to use.", progress, Cancel, errors through
  `AiText.Friendly` = the engine's message translated), `AiVoiceWindow` (Edit: name/emoji for imported voices, own
  quality, source/licence), `SettingsWindow.Ai.cs` (quality, device, Hear me plays the AI voice, Test my PC, Remove AI
  mode… with "also delete the AI voices I added"), `CreditsWindow` (`THIRD-PARTY-NOTICES.md`, embedded).
- **Sessions** (`Runtime/OrtRuntime.cs`): `AiDevice` Cpu / Gpu (DirectML on Windows, CoreML on macOS, CUDA on Linux,
  each tried and falling back to the CPU when missing or refused, logged; DirectML tries the adapters in
  `Runtime/GpuAdapters.cs` order: DXGI's high-performance one (a laptop's discrete GPU, not adapter 0 that shows the
  desktop), then other hardware adapters by their own memory, never a software one) / Auto; "auto" in the settings = the CPU
  until the benchmark found the GPU better. Intra-op threads = physical cores ≤ 6, spinning off. Runs are serialised
  per model (DirectML). `Engine/ModelCache.cs` shares loaded models between streams (leases; unloaded 3 min after
  the last one).
- **Store** (`Store/`): the pack is `<data>/AI` (`AiPack.cs`: `engine/hubert.onnx` [+ `hubert_int8.onnx`],
  `engine/rmvpe_f16.onnx` (RMVPE with its big weights stored as fp16: `OnnxRewriter.StoreHalf` at build time,
  `ExpandHalf` back to the same fp32 graph at load via `AiPack.ReadPitchModel`; tensors under 4096 values stay fp32;
  `rmvpe.onnx` as published is still read), `engine/rvc-v2-40k.pth` = the base generator, optional `engine/templates/` overriding the
  built-in ones, `runtime/`, `voices/<id>/voice.json` + files). `VoiceManifest` (id — persisted, never renamed — name,
  emoji, kind `speaker`/`pth`/`onnx-pair`/`onnx-single`, rate, `TargetHz`/`Key`, source/licence/attribution, files with
  SHA-256). `VoiceImporter`: `.pth` (checked by building and running it once), `.onnx` (recognised by its inputs,
  rate probed), `.index`, `.zip` (only model files are taken, under our own names; G_/D_ training checkpoints skipped),
  links (`Downloader`: https only — http to localhost for tests —, Hugging Face `/blob/` → `/resolve/`, size cap
  1.5 GB, SHA-256, progress, cancel, nothing left behind); a voice is added whole or not at all.
- **`.pth` without PyTorch** (`Import/`): `TorchCheckpoint` (zip + a pickle machine for what torch.save writes; nothing
  is executed), `WeightMap` (weight norm folded along the dim g keeps; prefixes matched), `OnnxRewriter.Fill` (a
  template's initializers replaced by the tensors, shape-checked, one allocation), `RvcTemplates` (v2 with pitch,
  32k/40k/48k; the rate from `sr`, the config or the first upsampler's kernel; unknown layouts or unused tensors →
  "isn't supported yet"). `SessionOptions.AddInitializer` must not be used for this (ORT has used the file's values).
- **Bundled voices** (`Voices/bundled-voices.json` → `PackBuilder.WriteBundledVoices`): blends of the base model's
  VCTK speaker vectors (MIT model, CC BY 4.0 data, attribution in each voice.json), each three speakers 0.6/0.25/0.15 (a
  main one and two near it but far from the other voices), the set of 8 picked as the least alike by WavLM speaker
  similarity and the MFCC print (`tools/ai/export/judge_voices.py` on `blends` renders; vs the earlier 2-way set on three
  recordings: highest similarity 0.933 → 0.909, mean 0.639 → 0.621, MFCC distance min 12.1 → 13.2, recognised 39 → 42/48),
  each with a natural pitch. Listening samples: `voice-samples/ai/` (git-ignored). `PackBuilder`
  makes the whole pack from `BaseModels` (RVC's Hugging Face files, SHA-256 = their LFS ids) with no Python.
- **Pack install** (`Store/PackInstaller.cs`): the pack is a release of its own (tag `PackSource.Tag` = `ai-pack-v1`;
  assets `EasyVocal-AI-win-x64.zip`, `-macos-arm64.zip`, `-macos-x64.zip`, `-linux-x64.zip`; SHA-256 from the asset's
  `digest` or a `<asset>.sha256` next to it, none = refused). The release is looked up through api.github.com (60 requests an hour per
  network without an account: shared addresses like the macOS CI runners get HTTP 403); when it refuses or can't be
  reached, `PackSource.DirectBase` (the release's direct links: `<asset>.sha256` + a one-byte range for the size) is
  used (`ApiFailure` says why). Each failure says what happened (no connection, timeout, too many requests, server
  error, refusal) and keeps the URL, status, rate-limit headers and body in its inner exception for the log. Downloaded to `<data>/AI.download/` (resumable with a
  range; the `.part` records the SHA-256 it is meant to become in `.part.sha256`, a part of another file is dropped;
  wrong bytes never resumed; a whole zip left from before is hashed off the UI thread, "Checking the download…"),
  unpacked into `AI.staging-*`, checked (HuBERT fp32 + int8, RMVPE (fp16-stored or not), base generator,
  voices), moved into `<data>/AI` part by part (imported `voices/user-*` untouched; a loaded runtime that can't be
  replaced — Windows, also after Remove left it half deleted — waits as `runtime.pending` with the `runtime.remove`
  marker, kept until `CleanLeftovers` swaps it in at the next start; `InstallAsync` returns true and the download card
  says to restart EasyVocal), then
  `AiVoiceService.PackChanged` re-probes ONNX Runtime (no restart). Remove deletes engine, bundled voices, runtime (a
  loaded one at the next start: `CleanLeftovers`). `Downloader`: https only (`AllowLoopbackHttp` for tests/e2e),
  idle timeout, Content-Disposition, `Sniff` by bytes. Untrusted sizes are capped (`TorchCheckpoint`: data.pkl 8 MB,
  objects, storages read only as far as tensors reach, 1.5 GB total; `FeatureIndex` sizes checked against the file,
  nprobe ≤ 8; zips: 2000 entries, only the picked files unpacked; ONNX with external data refused).
- **Benchmark** (`AiBenchmark.cs`): each level on the CPU (and the GPU), real time = mean block ≤ 75 % of the block and
  p95 ≤ 110 %; suggests the best real-time level (GPU wins ties); `SaveTo(AiSettings)`. A level counts on the GPU only when every
  model ran there (`ProviderOf`); its latency includes the resamplers. Run once after the pack is installed and from
  Settings → Test my PC.

## Avalonia notes (learned in the port)

- Dialogs are awaited: `await window.ShowAsync(owner)` then read `Accepted` (no nested message loop). Without a visible owner a
  dialog goes modal over the main window when that is on screen (`AppWindow.MainWindowSource`), else opens on its own;
  `SingleKind` (setup, settings, update) keeps one of a kind open (`WasDuplicate`). Code after an await must check
  `controller.IsDisposed` (EasyVocal may have quit meanwhile). With no tray icon `MainWindow.CanHideToTray` is false and
  closing minimises. The smoke and e2e tests fail when an exception reaches the UI thread (`AppSession` counts them), and
  `Ui.Click` refuses a disabled or hidden button. Closing a dialog
  can run the awaiting code right away (inline continuation): set any flag the caller checks *before* the click that
  closes it. In the smoke test an `async` step must go through `DialogAsync` (`Dialog` takes an `Action`, so an async
  lambda there becomes `async void` and the window closes under it).
- Fire-and-forget work from the UI (a dialog opened from a click, a file loaded for an editor) goes through
  `FireAndForget.Watch(task, "what")`, never `_ = task`: a failure is logged and counts in the smoke/e2e tests at once
  (they `SettleBackgroundAsync` before checking). A dialog over a minimised owner restores the owner first. Settings has
  a "Quit EasyVocal" button (`AppController.RequestQuit` → the session's normal quit), for desktops without a tray.
  macOS: a Dock click while the window is hidden shows it (`IActivatableLifetime.Activated`, `ActivationKind.Reopen`).
- Text in XAML: `{loc:T 'English text'}` (`xmlns:loc="using:EasyVocal"`); `ToolTip.Tip`, `IsVisible` (no Visibility).
- Styles: control themes in `Theme.axaml` (`Theme="{StaticResource AccentButton}"`, or `Res.Theme("CardButton")` in
  code); a subclass of a themed control needs `StyleKeyOverride` (AppWindow returns `typeof(Window)`). Emoji
  TextBlocks use `Classes="emoji"` (the colour font, normal weight: a bold parent made it fall back to black and white).
- `TextBox.TextChanged` may arrive after the change: tell a name the code set from one the person typed by its text.
- Drag and drop: `DragDrop.AllowDrop` + `e.DataTransfer.TryGetFiles()` (the `e.Data` API is obsolete in 11.3).
- `AVLN3001` is silenced in the csproj: the windows take the controller in their constructor (the runtime XAML loader
  isn't used).

## Global constraints

- Windows 10/11, macOS (arm64 and x64) and Linux x64 (PipeWire or PulseAudio; hotkeys need X11, none on Wayland).
  Downloads (2.0.0): win-x64 zip 38.6 MB, macOS arm64 / x64 .app zips 38.5 / 39.9 MB, Linux tar.gz 36.8 MB (limit 100 MB).
  AI pack (`ai-pack-v1`, ≤ ~1 GB): win-x64 540 MB, macos-arm64 535 MB, macos-x64 536 MB, linux-x64 533 MB (MiB; ~790 MB
  unpacked).
  The macOS .app is **unsigned** (no codesign on Windows; the .NET SDK strips the apphost's signature when it bundles it
  off a Mac), so Apple Silicon needs the README's one-time `codesign --force --deep --sign -`; Intel just needs
  right-click → Open. Latency off Windows: PortAudio / PulseAudio blocks of 10 ms, about 20 ms queued.
  Core and the app stay portable `net8.0`: nothing
  Windows-only in them unless behind `OperatingSystem.IsWindows()` (CA1416 warns); put platform code in
  `EasyVocal.Platform.Windows` (and the macOS/Linux projects) behind a `Core/Platform` interface. No WPF/WinForms.
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
- Dependencies: only permissive packages (Core: NAudio.Core, NAudio.Vorbis, NLayer; Windows platform: NAudio;
  macOS/Linux: org.k2fsa.portaudio.runtime.* (PortAudio's native library, MIT, packaged Apache-2.0) with our own
  P/Invoke; app: Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent (MIT); tests: xUnit, Avalonia.Headless.XUnit). The
  system's own libraries are loaded at run time: CoreAudio, Carbon, AVFoundation (macOS); libpulse-simple, libX11 (Linux).
  AI engine: `Microsoft.ML.OnnxRuntime.Managed` (MIT); its natives (`Microsoft.ML.OnnxRuntime`, `.DirectML`, MIT) only in
  tests, tools and Debug builds, never in the release downloads (the AI pack carries them). AI models: MIT (RVC,
  HuBERT, RMVPE: Apache-2.0); voice data CC BY 4.0 (VCTK) with attribution. GPL model exports (w-okada, MoeSS) are not used.
- Audio thread: no allocation per block, no locks held while doing DSP, no LINQ.
- VB-CABLE is not bundled (donationware); the app downloads the official installer and verifies its signature.
  BlackHole (GPL) is not bundled either: Homebrew installs it, or the person does from its page. On Linux nothing is
  installed: EasyVocal loads two pactl modules while it runs. Off Windows EasyVocal never changes the system's default
  devices (`SetDefault` is false), and only VB-CABLE's install runs the remember / put back / Fix logic.

## Hard rules

- **Persisted ids are never renamed** (voice ids like `"movie-robot"`, sound ids, settings keys, general hotkey keys
  `toggleFilter`/`stopSounds`/`muteMic`). Unknown or missing settings keys get defaults.
- **Versioning X.Y.Z** lives in `Directory.Build.props` (`<Version>`). Every version is a tag `vX.Y.Z` and a GitHub
  release made by `tools/Publish-Release.ps1`; the asset names `EasyVocal-win-x64.zip`, `EasyVocal-macos-arm64.zip`,
  `EasyVocal-macos-x64.zip`, `EasyVocal-linux-x64.tar.gz` are fixed (README buttons, each platform's `AssetNames`;
  `ReleasePackagingTests` checks all three agree).
- **Encoding**: sources are UTF-8 without BOM and contain emoji. Never rewrite files with PowerShell
  `Get-Content | Set-Content` / `-replace` (it double-encodes). Use an edit tool or `sed`. After bulk edits grep for
  mojibake: `â€|Ã©|ðŸ|ï¼`.
- Every user-visible text goes through `L.T(...)`; add the pt-BR entry in `Lang/pt-BR.json`.
- **A sound's original file is never modified.** The sound editor saves a new WAV in `Sounds\` ("<original>
  (edited).wav"), points `SoundItem.FileName` at it and keeps the original in `SoundItem.OriginalFileName` ("Back to
  original"). Never delete a file another sound still uses (`ItemOps.IsFileUsed`).
- **Windows fit small screens** (1366×768 at 125 % is ~580 px tall): every window derives from `AppWindow` and calls `Place(key, controller)` (→ `WindowPlacer`), is
  resizable where it makes sense, has MinWidth/MinHeight, and keeps long content in a ScrollViewer with its
  Save/Close buttons outside it. The smoke test renders each one at its minimum size (`smoke_small_*.png`,
  `smoke_audio_editor_small.png`) and checks those buttons are on screen.
- **The updater never runs in the tests and never installs from a source build.** Smoke and e2e build no
  `UpdateCoordinator` (the smoke test uses a fake `IUpdateSource` and `IHelperLauncher`); a build under the repo
  (bin\Debug, publish\win-x64) is a source build. The helper swaps only the install root (`EasyVocal.exe`; the `.app`; the `EasyVocal/` folder) (a `.old` copy until the new one
  is in place, put back if the copy fails), waits up to 60 s for EasyVocal to close, then opens it again; the data folder
  is never touched. Settings keys: `CheckForUpdates`, `SkippedUpdateVersion`.
- **Only a shipped install is ever replaced or deleted.** The release's `easyvocal-files.txt` marks its install root
  (next to EasyVocal.exe; `EasyVocal.app/Contents/Resources/`; the `EasyVocal/` folder). macOS/Linux self-update only
  from a marked root named right (a `*.app` whose `Contents/MacOS/EasyVocal` is the running program; a folder called
  `EasyVocal`), the download must carry the marker, the sh helper checks both again (exit 5), and `CleanStale` deletes
  `<root>.old` only when it is marked (`IUpdaterPlatform.IsInstallRoot`). A program run loose (Downloads) gets the
  release page. Windows swaps only its exe and puts the zip's list next to it.
- **Never push, tag or publish a release without the owner's word.**
