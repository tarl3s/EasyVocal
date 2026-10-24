# EasyVocal

**A free, light voice changer and soundboard for any call.** Talk as a robot, a monster or a chipmunk on Discord,
WhatsApp Desktop, in games, anywhere that uses a microphone, and play sounds into the call with one key.
No ads, no paywall, nothing locked.

[Português (Brasil)](README.pt-BR.md)

[**Download EasyVocal for Windows**](https://github.com/tarl3s/EasyVocal/releases/latest/download/EasyVocal-win-x64.zip)
(about 58 MB, Windows 10 or 11, nothing else to install)

## Set it up in 3 steps

1. **Install EasyVocal.** Unzip the download anywhere and open `EasyVocal.exe`.
2. **Install VB-CABLE from inside the app.** On the first run EasyVocal explains it and has an **Install** button.
3. **Pick "CABLE Output" as the microphone** in your call app (Discord, WhatsApp Desktop, your game...). EasyVocal
   shows a short guide for each app.

That's it: whatever you do in EasyVocal is what the other side of the call hears.

> **The voice only works while EasyVocal is open.** Closing the window sends it to the tray (next to the clock),
> where it keeps working. If you quit it, your calls hear nothing until you open it again. You can tick **Start
> with Windows** in the settings so you never forget.

## What you get

- **14 built-in voices:** Normal, Female, Male, Robot, Chipmunk, Monster, Alien, Radio, Demon, Cave, Dwarf, Giant,
  Ghost and Movie robot.
- **Your own voices:** a small editor with 7 plain sliders (Deeper/Higher, Voice body, Robot, Echo, Old radio,
  Distortion, Whisper). Hear it live with **Hear me** while you drag.
- **A soundboard:** 10 starter sounds that come with the app, and your own: add any MP3, WAV or OGG (or drag and
  drop it), give it a name, an emoji and a volume. Press a playing sound again to stop it. Sounds can overlap.
- **Global hotkeys** for every voice and every sound, plus turning the voice on/off, stopping all sounds and muting
  the mic. They work while a game is fullscreen. (A plain key without Ctrl or Alt stops typing that character
  elsewhere while EasyVocal runs, so the app warns you.)
- **Tray icon** with a quick voice menu, and **Start with Windows** (off by default).
- **English and Português (Brasil).**
- **Light:** small download, almost no CPU when idle, a few percent while you talk.
- **OFF or Normal = your clean voice.** Your friends still hear you.
- **AI voices: coming later** as a separate optional download.

## Credits

- **VB-CABLE** is a separate free product by [VB-Audio](https://vb-audio.com/Cable/) (donationware). EasyVocal does
  not include it: it downloads the official installer from VB-Audio and starts it for you.
- Audio engine: [NAudio](https://github.com/naudio/NAudio) (MIT) and NAudio.Vorbis.
- EasyVocal is MIT licensed. See [LICENSE](LICENSE).

## For developers

```
dotnet build
dotnet test
dotnet run --project src/EasyVocal
```

More in [AGENTS.md](AGENTS.md). Release: `tools/Publish-Release.ps1`.
