# Third-party notices

EasyVocal itself is MIT-licensed (see `LICENSE`). It uses the following work by others. The app shows this file in
Settings → "Credits and licences".

## Libraries in every download

- **Avalonia** (Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent) — MIT. Copyright (c) AvaloniaUI OÜ and contributors. https://github.com/AvaloniaUI/Avalonia
- **NAudio** (NAudio, NAudio.Core) — MIT. Copyright (c) 2020 Mark Heath. https://github.com/naudio/NAudio
- **NVorbis** / **NAudio.Vorbis** — MIT. Copyright (c) Andrew Ward and contributors. https://github.com/NVorbis/NVorbis
- **NLayer** — MIT. Copyright (c) Mark Heath, Andrew Ward and contributors. https://github.com/naudio/NLayer
- **PortAudio** (macOS and Linux; packaged by k2-fsa as org.k2fsa.portaudio.runtime, Apache-2.0) — MIT-style PortAudio licence. Copyright (c) 1999-2011 Ross Bencina and Phil Burk. http://www.portaudio.com
- **ONNX Runtime** managed API (Microsoft.ML.OnnxRuntime.Managed) — MIT. Copyright (c) Microsoft Corporation. https://github.com/microsoft/onnxruntime

## The AI pack (downloaded only when AI mode is turned on)

- **ONNX Runtime** native library (CPU, DirectML on Windows) — MIT. Copyright (c) Microsoft Corporation.
  ONNX Runtime's own third-party notices (`ThirdPartyNotices.txt`) ship with it in the AI pack's `runtime` folder.
  https://github.com/microsoft/onnxruntime/blob/main/LICENSE
- **RVC — Retrieval-based Voice Conversion WebUI** (the RVC v2 voice conversion network and its pretrained base
  generator f0G40k) — MIT. Copyright (c) 2023 liujing04, 源文雨 and the RVC-Project contributors.
  https://github.com/RVC-Project/Retrieval-based-Voice-Conversion-WebUI/blob/main/LICENSE
- **HuBERT base** content encoder (fairseq) — MIT. Copyright (c) Facebook, Inc. and its affiliates.
  https://github.com/facebookresearch/fairseq/blob/main/LICENSE — trained on **LibriSpeech** (Vassil Panayotov,
  Guoguo Chen, Daniel Povey, Sanjeev Khudanpur), CC BY 4.0, https://www.openslr.org/12
- **RMVPE** pitch estimator — Apache License 2.0. Copyright (c) Haojie Wei et al. (Yxlllc/RMVPE).
  https://github.com/Dream-High/RMVPE — NOTICE: "RMVPE: A Robust Model for Vocal Pitch Estimation in Polyphonic
  Music" (Wei et al., Interspeech 2023); this distribution converts the published weights to ONNX (fp32/fp16) and
  makes no other change.
- **CSTR VCTK Corpus** (the voice data the base generator was trained on, whose speaker vectors the bundled AI voices
  blend) — CC BY 4.0. Junichi Yamagishi, Christophe Veaux, Kirsten MacDonald; University of Edinburgh, The Centre for
  Speech Technology Research. https://doi.org/10.7488/ds/2645 — https://creativecommons.org/licenses/by/4.0/
  Modified: each bundled voice is a weighted blend of several speakers' voice vectors, made to sound like none of
  them in particular.

## Tools and tests only (not in the downloads)

- xUnit (Apache-2.0), Avalonia.Headless (MIT), Microsoft.NET.Test.Sdk (MIT), coverlet (MIT); PyTorch, Transformers
  and ONNX (BSD/Apache/MIT) in `tools/ai/export` at development time only.
