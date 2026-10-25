# EasyVocal 1.0.0 — macOS and Linux

Date: 2026-10-25. The owner asked for macOS and Linux versions, a version bump straight to 1.0.0 when they ship, no VM
unless needed, and the full gate (tests, code review, simplify, E2E) before publishing. Decisions below are Claude's
calls (the owner delegated them).

## Intent
Same EasyVocal — free, light voice changer + soundboard for any call app — on Windows, macOS and Linux, with one
codebase. Windows must not get worse: every 0.2.0 feature keeps working and the Windows suites stay green.

## Decisions
- **UI: Avalonia 11 (MIT) on all three platforms**, replacing WPF. One UI, not two. Keep the same look (dark palette
  tokens, cards, tabs, banners), the same windows and the same behaviour; port window by window. Translations
  (L.T + Lang/pt-BR.json) unchanged.
- **Core becomes portable (`net8.0`)**: model, storage, DSP, engine, hotkey logic, localizer, audio editing. Windows-only
  bits move behind interfaces into platform projects.
- **Audio I/O**: Windows keeps NAudio WASAPI (proven, low latency). macOS and Linux use PortAudio through a small,
  permissively licensed binding that ships native libraries for osx-x64/osx-arm64/linux-x64 (e.g. PortAudioSharp2) —
  or a thin P/Invoke of our own if the packages don't fit. Device listing, default device, device-change detection.
- **Decoding off Windows**: WAV (own reader), MP3 (NLayer, MIT), OGG (NVorbis), FLAC (a managed decoder if a small
  permissive one exists; otherwise not on mac/Linux), and anything else through `ffmpeg` when it is installed
  (detected; friendly message otherwise). Windows keeps Media Foundation.
- **Virtual microphone**:
  - Windows: VB-CABLE as today (installer flow, Authenticode check, default-device restore).
  - macOS: **BlackHole 2ch** (free). Setup explains it in one sentence, button opens the official download page
    (existential.audio/blackhole) and, if Homebrew is present, offers `brew install blackhole-2ch`; detects
    "BlackHole 2ch" as the cable; the call app picks "BlackHole 2ch" as its mic. Microphone permission prompt handled
    (Info.plist NSMicrophoneUsageDescription).
  - Linux: **no driver** — EasyVocal creates a virtual mic with PipeWire/PulseAudio (`pactl load-module
    module-null-sink sink_name=easyvocal_out` + `module-remap-source master=easyvocal_out.monitor
    source_name=easyvocal_mic source_properties=device.description=EasyVocal-Microphone`), removes it on quit; the
    call app picks "EasyVocal Microphone".
- **Global hotkeys**: Windows RegisterHotKey; macOS Carbon `RegisterEventHotKey` (no accessibility permission needed);
  Linux X11 `XGrabKey`; on Wayland, hotkeys are shown as unavailable with a sentence explaining why (the app still
  works with clicks).
- **Tray**: Avalonia TrayIcon everywhere (on GNOME without an AppIndicator extension, closing the window minimises
  instead of hiding — detected best-effort).
- **Start at login**: Windows Run key; macOS LaunchAgent plist in ~/Library/LaunchAgents; Linux
  ~/.config/autostart/easyvocal.desktop. **Start menu shortcut**: Windows .lnk; Linux ~/.local/share/applications
  .desktop (same "ask once" flow); macOS: not applicable (the .app goes to Applications; the README says so).
- **Updater**: per-platform asset (`EasyVocal-win-x64.zip`, `EasyVocal-macos-arm64.zip`, `EasyVocal-macos-x64.zip`,
  `EasyVocal-linux-x64.tar.gz`); Windows swap as today; macOS replaces the .app bundle; Linux replaces the folder;
  both relaunch. If self-update isn't possible (e.g. read-only location), open the release page instead.
- **Packaging**: self-contained single folder per RID; macOS `.app` bundle (Info.plist, icon .icns, unsigned — README
  explains right-click → Open the first time); Linux tar.gz with the executable, icon and a .desktop file (AppImage
  optional, only if simple). Size goal per download < ~100 MB.
- **CI instead of a VM**: GitHub Actions workflow on windows-latest, macos-latest (arm64) and ubuntu-latest: build,
  unit tests, and a headless E2E (Avalonia.Headless + fake audio devices) on all three. On a `v*` tag: build all
  assets and attach them to the release. The Windows-only WASAPI/WPF-specific tests run only on Windows.
- **README**: one big download button per OS at the top (shields.io badges), short per-OS "how to open" and
  virtual-mic steps.
- **Version**: 1.0.0.

## Non-goals
App Store / notarisation, Flatpak/Snap store listing, Wayland global hotkeys, ARM Linux, AI voices.

## Phases (each a reviewed task; Windows suites green after every phase)
1. Portable Core: retarget to net8.0, move Windows-only code (Media Foundation, WASAPI types) out; platform
   abstractions (audio backend, decoder, hotkeys, tray/startup/shortcut services, virtual-mic provider).
2. Avalonia UI port of every window, WPF removed; smoke + E2E rewritten on Avalonia (headless where possible) and
   green on Windows with the WASAPI backend.
3. macOS + Linux backends: PortAudio I/O, decoders, virtual mic (BlackHole / pactl), hotkeys, startup, shortcut,
   updater asset logic; unit tests with fakes.
4. Packaging + GitHub Actions (3-OS CI, release job), README buttons.
5. Gate: tests on all 3 OS via CI, code review, simplify, E2E; publish 1.0.0.
