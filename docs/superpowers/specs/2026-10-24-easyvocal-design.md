# EasyVocal — design

Date: 2026-10-24. Status: draft for owner approval.

## Intent

EasyVocal is a **free, lightweight Windows app that changes your voice live in any call** — Discord, WhatsApp
Desktop, games, anything that uses a microphone — and plays sounds (memes, any audio the person adds) into the
call. The owner tried voice changers before and found them all paid and heavy; EasyVocal wins by being free for
real (open source, no ads, no paywalled filters) and running on any PC without weighing it down. The audience is
people who want to have fun on calls, not audio people: everything is buttons, emojis and simple sliders.

## Goals

1. Voice filters applied in real time to the person's microphone and heard by the other side of any call.
2. A soundboard whose sounds are mixed into the same microphone signal (and, optionally, into the person's own
   headphones).
3. Lightweight by default: small download, low CPU, starts fast, sits in the tray.
4. Customizable: the person creates their own filters with simple sliders and their own sound buttons with any
   audio file.
5. Global hotkeys for every filter and every sound, working while a game is fullscreen.
6. An optional, separately downloaded **AI pack** for realistic voice conversion.

## Non-goals (v1)

- Mac, Linux, phones. WhatsApp only works through WhatsApp Desktop/Web on the PC; a call answered on the phone is
  out of scope.
- An "advanced mode" for audio experts (EQ curves, effect chains, plugin hosting). The owner explicitly wants only
  simple sliders.
- Sharing/importing filters between friends, online catalogs, accounts.
- Our own signed audio driver.
- Recording, streaming tools, video.

## Anti-goals (fails even if it technically works)

- Anything paid, locked, nagging for money, or with ads.
- Heavy: a large install, noticeable CPU use in games, or long startup. The default install must not include the AI
  engine or any model.
- Copyright trouble on a public repo: shipping real celebrity/character voice models or copyrighted meme audio.
- Audible delay that makes talking awkward on a call (target: under ~50 ms added end-to-end with classic filters).
- Breaking the person's microphone: when EasyVocal is off or the filter is "Normal", the call must still get the
  clean voice through the virtual mic.

## Constraints and decisions

### Platform and stack (Claude's call)
Windows 10/11 only, **C# / WPF / .NET 8**, same as the owner's EasyEngine, so the same habits apply: UI text in
English through a translation layer, dark theme by default, readable contrast, public GitHub repo under MIT,
every version a tagged GitHub release with a downloadable zip. Unlike EasyEngine, a small, permissively licensed
audio library (e.g. NAudio, MIT) is acceptable — writing WASAPI interop by hand is not worth it.

### How the voice reaches the call (Claude's call)
Windows apps only see a voice through a microphone device, so EasyVocal routes its output to the free third-party
virtual cable **VB-CABLE** (VB-Audio). EasyVocal does not bundle it (its license is donationware with restricted
redistribution): on first run, if no "CABLE Input/Output" device exists, a friendly setup screen explains it in
one sentence and has an **Install** button that downloads the official installer from vb-audio.com and launches
it, then tells the person to pick **"CABLE Output"** as the microphone in Discord/WhatsApp (with a short picture
guide per app). The person can also choose any other virtual device in Settings.

Audio path: real microphone (WASAPI capture, chosen in Settings) → filter → mixer (+ soundboard) → virtual cable
input. In parallel, optional monitor output to the person's headphones: "🎧 Hear me" (hear your own filtered voice,
to test) and "hear sounds I play" (on by default).

*Claude's call — when EasyVocal isn't running:* the voice only reaches the call while the app is open (it is a tray
app, no background service). Quitting from the tray asks "Calls set to CABLE Output will hear nothing until you
open EasyVocal again — quit anyway?" (with "don't ask again"), and the first-run guide offers "Start with Windows"
as a recommended checkbox there. No watchdog or "Listen to this device" tricks in v1.

### Size (Claude's call)
Shipped self-contained (no .NET install needed — a runtime prerequisite is worse friction for this audience than
megabytes), as one zip, target **under ~80 MB download** (voice-changer competitors are larger), idle CPU near zero
and a few percent while talking. The AI pack is a separate download of whatever size it needs.

### Screen layout (owner decided: tabs)
- Top bar, always visible: EasyVocal name, big **ON/OFF** (off = clean voice passes through), live mic level
  meter, **🎧 Hear me** toggle, settings gear.
- Two tabs: **🎭 Voices** and **🔊 Sounds**, each a grid of large buttons (emoji + name), the hotkey shown in the
  button's corner, the active voice highlighted, a dashed **＋ New filter / ＋ Add sound** button at the end.
- Right-click (or a ⋯) on a button: edit, set hotkey, duplicate, delete. Built-in items can be duplicated and hidden
  but not destroyed.
- Closing the window minimizes to the tray; the tray menu lists voices to switch quickly and Quit. Option to start
  with Windows (off by default).

### Voices
Two layers (owner decided):
- **Classic (default, built in):** DSP effects that run on any PC. At least: Normal, Female, Male, Robot, Chipmunk,
  Monster, Alien, Radio, Demon, Cave/Echo, plus a few generic "characters" built from the same effects (e.g. dwarf,
  giant, ghost, movie robot). They sound like effects, not like a real person — that's fine; the point is fun.
- **AI pack (optional download):** a **"Download AI mode"** button installs a separate engine for realistic voice
  conversion (RVC-style models). It is never part of the base download. EasyVocal ships no models of real people
  or copyrighted characters; the person imports model files they obtained themselves, and each becomes a voice
  button like any other. The UI warns that it needs a good graphics card. *Claude's call:* the AI pack comes in a
  later phase, after the classic app is solid; v1 only reserves its place in the UI and the data model.

### Custom filters (owner decided: only simple sliders)
"＋ New filter" opens a small editor: a name, an emoji, a "start from" choice (Normal or any existing voice), and a
handful of plain-language sliders, heard live through "Hear me" while dragging:
**Deeper ↔ Higher** (pitch), **Voice body** (formant — makes male/female more convincing), **Robot**, **Echo**,
**Old radio**, **Distortion**, **Whisper/breath** (exact set is the builder's call, but keep it to about 6–8 sliders
with friendly names, no units, no dB/Hz).

### Sounds
- Ships with a starter pack of **generic** sounds that are **free to redistribute** (CC0 or generated by us):
  airhorn, laugh, applause, fail/trombone, drum roll, crickets, ding, etc. *Owner decided:* no recognisable meme
  clips are shipped; people add the famous memes themselves as MP3/WAV/OGG files.
- "＋ Add sound": pick or drag-drop an audio file (WAV, MP3, OGG at least), give it a name and emoji, a volume,
  optional hotkey. The file is copied into EasyVocal's data folder so moving the original doesn't break it.
- Pressing a playing sound's button again stops it; a global "stop all sounds" action exists. Several sounds can
  overlap.

### Hotkeys (owner decided: customizable per voice and per sound)
- Every voice and sound can get a global hotkey (e.g. `7` plays the airhorn, `F1` turns on Robot). General actions
  too: filter off/on, stop all sounds, mute mic.
- They work while EasyVocal is minimized and a game is fullscreen.
- A single plain key is allowed but the app warns that the key stops typing that character elsewhere while
  EasyVocal runs, and suggests Ctrl+key or the numpad. Conflicting hotkeys are refused with a clear message.

### Data
Settings, voices, sound list and hotkeys live in `%APPDATA%\EasyVocal\` (JSON), sound files in a subfolder.
Unknown/missing keys get defaults so older settings keep loading.

### Languages
English UI text through a translation layer, Brazilian Portuguese shipped from day one (the owner's language);
more languages are welcome later.

## Left to the builder

- DSP algorithms (pitch/formant shifting method, buffer sizes) as long as the latency and CPU goals hold.
- Exact list and tuning of built-in voices and sliders; icons and visual polish within the dark theme.
- Library choices (audio I/O, decoding), as long as they are small and permissively licensed.
- Test strategy (headless tests of the DSP chain and the mixer are expected; UI snapshot tests welcome).
- Release packaging and an in-app updater (the EasyEngine updater is a good model).
- How the AI pack is built and run (later phase).
