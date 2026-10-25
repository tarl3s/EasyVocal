# EasyVocal

<!-- Download buttons. macOS and Linux buttons go next to this one when those versions exist. -->
<p align="center">
  <a href="https://github.com/tarl3s/EasyVocal/releases/latest/download/EasyVocal-win-x64.zip"><img alt="⬇ Download for Windows" src="https://img.shields.io/badge/%E2%AC%87%20Download-for%20Windows-2ea44f?style=for-the-badge&logo=windows&logoColor=white" height="48"></a>
</p>
<p align="center">Unzip it anywhere and double-click <code>EasyVocal.exe</code> (about 58 MB, Windows 10 or 11, nothing else to install).</p>

**A free, light voice changer and soundboard for any call.** Talk as a robot, a monster or a chipmunk on Discord,
WhatsApp Desktop, in games, anywhere that uses a microphone, and play sounds into the call with one key.
No ads, no paywall, nothing locked.

[Português (Brasil)](README.pt-BR.md)

## Set it up in 3 steps

1. **Install EasyVocal.** Unzip the download anywhere and open `EasyVocal.exe`.
2. **Install VB-CABLE from inside the app.** On the first run EasyVocal explains it and has an **Install** button.
   If you hear nothing after installing it (VB-CABLE can make itself the PC's default speaker), EasyVocal puts your
   sound back on your headphones or speakers, or offers a **Fix** button that does it.
3. **Pick "CABLE Output" as the microphone** in your call app (Discord, WhatsApp Desktop, your game...). EasyVocal
   shows a short guide for each app.

That's it: whatever you do in EasyVocal is what the other side of the call hears.

> **The voice only works while EasyVocal is open.** Closing the window sends it to the tray (next to the clock),
> where it keeps working. If you quit it, your calls hear nothing until you open it again. You can tick **Start
> with Windows** in the settings so you never forget.

## What you get

- **14 built-in voices, each with its own character:**
  - **Normal**: your own voice, untouched.
  - **Female** / **Male**: higher, with a lighter voice body and less low end / deeper, with a bigger body and more
    chest.
  - **Robot**: your melody flattened to almost one note, metallic and buzzing.
  - **Movie robot**: a synthesizer buzz on one fixed note that says your words (vocoder).
  - **Chipmunk**: very high and tiny.
  - **Monster**: deep, rough and rattling (a growl), heavy bass, almost no room.
  - **Demon**: your voice plus a second voice an octave below it, distorted, in a dark cave.
  - **Giant**: a huge body, a bit deeper, in an enormous hall; clean.
  - **Dwarf**: a little deeper with a small, gruff body.
  - **Alien**: a wavering, warbling pitch with a metallic edge and a spacey echo.
  - **Ghost**: an airy whisper with a slight waver, floating in a big soft room.
  - **Radio**: a thin old radio, a little crunch and crackle.
  - **Cave**: your normal voice with echoes off rock walls.
- **Your own voices:** a small editor with 9 plain sliders (Deeper/Higher, Voice body, Robot, Echo, Old radio,
  Distortion, Whisper, Growl, Big room). Start from any built-in voice and it keeps its character. Hear it live with
  **Hear me** while you drag.
- **A soundboard:** 10 starter sounds that come with the app, and your own: add a sound file (MP3, WAV, OGG, FLAC,
  M4A, AAC, WMA, AIFF) or the sound of a video (MP4, MOV, WMV...), or just drag and drop it, then give it a name, an
  emoji and a volume. Press a playing sound again to stop it. Sounds can overlap. Tick **🎭 Sounds use the voice** and
  your sounds go through the voice too (switch voices while a sound plays and it changes).
- **A sound editor** (**✂ Edit audio...** on any sound): see the wave, pick a part with the mouse and hear it on
  your headphones, then cut it, keep only that part or split the sound in two; make it faster or slower (keeping the
  pitch or not), higher or lower, louder, quieter or as loud as possible; fade in or out, play it backwards, add an
  echo, run it through any of your voices, or cut the silence at the start and end. Undo and redo as much as you
  like. Your original file is never changed: **Back to original** is always one click away, and **Save a copy as...**
  exports WAV or MP3.
- **Global hotkeys** for every voice and every sound, plus turning the voice on/off, stopping all sounds and muting
  the mic. They work while a game is fullscreen. (A plain key without Ctrl or Alt stops typing that character
  elsewhere while EasyVocal runs, so the app warns you.)
- **Tray icon** with a quick voice menu, and **Start with Windows** (off by default).
- **Start menu shortcut**: the first time you open EasyVocal it asks whether to add it to the Start menu.
- **Updates itself:** EasyVocal tells you when there's a new version and updates itself with one click (or skip that
  version; you can turn the check off in the settings).
- **Fits small screens:** every window can be resized, scrolls instead of cutting things off, opens inside your
  screen and remembers where you left it.
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
dotnet test                                            # includes the end-to-end test (~40 s, shows the app)
dotnet run --project src/EasyVocal -- --data <scratch folder> --e2e
dotnet run --project src/EasyVocal
```

More in [AGENTS.md](AGENTS.md). Release: `tools/Publish-Release.ps1`.
