# Third-party components and licences

This repository contains original C# code plus a few third-party components (NuGet packages, ported
algorithms, reference implementations of published formats). The list below records the licence and the
obligations for each. **Keep this file and the full licence texts in `docs/licenses/` when you
publish or redistribute the project.**

---

## 1. Runtime dependencies (bundled into the CLI / GUI / APK)

| Component | Version | Licence | Copyright | Used for |
| --- | --- | --- | --- | --- |
| [.NET 10](https://github.com/dotnet/runtime) / `System.Drawing.Common` | 8.0.0 | MIT | © .NET Foundation and Contributors | Desktop image decoding (GDI+) |
| Android `BitmapFactory` | Apache-2.0 (platform API) | — | © The Android Open Source Project | Phone image decoding |
| [NLayer](https://github.com/naudio/NLayer) | 3.0.0 | MIT | © 2011 Mark Heath | MP3 decoding |
| [NVorbis](https://github.com/NVorbis/NVorbis) | 0.10.5 | MIT | © 2020 Andrew Ward | OGG Vorbis decoding |

- **NLayer**: full text in [`docs/licenses/NLayer-MIT.txt`](docs/licenses/NLayer-MIT.txt).
- **NVorbis**: full text in [`docs/licenses/NVorbis-MIT.txt`](docs/licenses/NVorbis-MIT.txt).

> Earlier versions bundled **SixLabors.ImageSharp** (Six Labors Split License, commercial use
> restricted) and **FlacBox** (no licence file upstream). Both were removed in 1.0: image decoding is
> now delegated to the host platform, and FLAC support was dropped. If you need FLAC, convert to WAV
> or MP3 first.

## 2. Ported / referenced algorithms

| Component | Licence | Copyright | Where it lives in this project |
| --- | --- | --- | --- |
| [LZHAM codec](https://github.com/richgel999/lzham_codec) | **Public Domain** (explicitly released 2020-09-15) | © Rich Geldreich et al. | `src/AeroflyConv.Core/Lzham/*` (pure C# decoder and encoder port) |
| [ARM astc-encoder](https://github.com/ARM-software/astc-encoder) | Apache-2.0 | © ARM Limited | `src/AeroflyConv.Core/Codecs/AstcEncoder.cs`, `AstcTables.cs` (block header bit layout and weight quantisation tables follow that implementation) |
| [AeroflyFS-Native-Converter](https://github.com/RishiK-06/AeroflyFS-Native-Converter) | MIT | © Rishi K | Reference implementation for the TTX / TSB / TTC / TM file formats |

- **LZHAM** is public domain, so attribution is not required; this project credits it anyway. Full
  text: [`docs/licenses/LZHAM-public-domain.txt`](docs/licenses/LZHAM-public-domain.txt).
- **ARM astc-encoder** is Apache-2.0: redistributions must keep the licence and NOTICE. Full text:
  [`docs/licenses/astc-encoder-Apache-2.0.txt`](docs/licenses/astc-encoder-Apache-2.0.txt). This
  project does not include astc-encoder sources; it aligns the block header layout, ISE coding and
  quantisation tables with that implementation.
- **AeroflyFS-Native-Converter** is MIT; see the acknowledgement in the root `LICENSE`.

## 3. What is *not* included

This repository contains **no** Aerofly FS game assets (`.ttx` / `.tmb` / `.tsb` / `.ttc` / `.tm*` or
livery textures) and no tooling or keys for the `.afsenc` container. Bring your own sample files and
do not commit them to a public repository.

## 4. This project

Original code (conversion engine, the C# LZHAM port, ASTC codec, GUI, Android app, documentation) is
released under the MIT licence — see `LICENSE`.
