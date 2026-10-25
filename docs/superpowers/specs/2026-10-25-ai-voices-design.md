# EasyVocal 2.0.0 — AI voices

Date: 2026-10-25. Owner decisions marked *(owner)*; the rest are Claude's calls.

## Intent
Realistic voice conversion: on a call the person should really sound like another person *(owner)*, not like an
effect. It is the biggest change since the start, so it ships as **2.0.0** *(owner)*.

## Goals
1. **Bundled AI voices** *(owner)*: a set of realistic generic voices (e.g. young woman, deep man, older man, older
   woman, teen, narrator) made only from voice data whose licence allows it (CC0 / CC-BY / own recordings) — never
   imitating a real known person. Each is a card in the Voices tab marked 🧠.
2. **Import voices** *(owner)*: by file (RVC `.pth` and/or `.onnx`, optional `.index`) or by pasting a link
   (Hugging Face and similar direct/zip links) that the app downloads and converts. Each import becomes a card.
3. **Works on any PC** *(owner)*: GPU when available (DirectML on Windows, CoreML on macOS, CUDA if present on Linux),
   CPU otherwise.
4. **Quality / weight setting** *(owner)*: Light / Balanced / Max — trades realism for CPU/GPU load and latency; the
   app benchmarks the PC once and suggests one; changeable in Settings and per voice if needed.
5. **Windows, macOS and Linux** *(owner)*, same engine everywhere.
6. **Optional download** (design since v1): the AI pack is installed from the app ("Download AI mode"), **≤ ~1 GB**
   *(owner)* with ~6–8 bundled voices; further voices download on demand. Normal EasyVocal stays light.

## Non-goals
Training your own voice *(owner: later)*; voice cloning from a few seconds of audio (zero-shot) unless it is clearly
better and licence-clean; celebrity/character voices shipped by us; mobile.

## Anti-goals
Robotic/metallic output that sounds like an effect; latency that breaks conversation (target ≤ ~250 ms Balanced on a
mid PC with GPU, ≤ ~400 ms Light on CPU); freezing the PC; shipping voices with unclear licences; breaking the
classic voices or the light default install.

## How (Claude's calls)
- **Model family: RVC v2** (the de-facto standard people already have models for) — content encoder (HuBERT/ContentVec)
  + pitch (RMVPE, fallback a lighter estimator on Light) + RVC generator, all run through **ONNX Runtime**
  (DirectML / CoreML / CPU execution providers) from C#. `.pth` imports are converted to ONNX — conversion needs
  PyTorch, which we don't ship: either convert in-app with a bundled minimal converter, or use a tiny one-off
  converter downloaded on demand; decide in the research phase (prefer: accept `.onnx` directly; for `.pth`, a
  pure-.NET reader of the state dict feeding an ONNX graph template — research feasibility first).
- **Streaming**: chunked inference with overlap/crossfade on a worker thread; the audio thread only reads ready
  output (never blocks); quality levels change chunk size, pitch estimator, index rate and model precision (fp16 on
  GPU).
- **Bundled voices**: research licence-clean pre-made RVC models; if none are clean enough, train them on a GPU
  machine from CC-BY data (e.g. LibriTTS-R / VCTK speakers) — that needs the owner's or a cloud GPU; keep attribution
  in the app's credits.
- **UI**: AI card becomes "🧠 AI voices": a sub-section in the Voices tab with bundled + imported voices, an
  "Add AI voice…" (file or link), quality picker, a first-run benchmark, a clear note "Use voices you have the right
  to use". Works with Hear me, hotkeys, "sounds use the voice".
- **Pack**: separate per-OS download hosted on GitHub Releases (asset per OS, < 2 GB GitHub limit), verified by
  SHA-256 from the release; installed into the data folder; removable.
- **Tests**: offline conversion tests (pitch follows input, speaker similarity rises towards the target via a small
  speaker-embedding model, no dropouts), latency/CPU budgets per quality level on CI runners, e2e with a tiny test
  model.

## Phases
1. Research + spike: ONNX RVC pipeline in C# end to end on one voice (offline, then streaming); measure latency/CPU on
   this PC (CPU-only and DirectML); licence research for bundled voices; .pth→.onnx path.
2. Engine integration (streaming, quality levels, benchmark), platform execution providers.
3. UI (AI section, import by file/link, quality, pack download/remove), bundled voices.
4. Packaging per OS, CI, docs; gate (tests, review, simplify, E2E) → 2.0.0.
