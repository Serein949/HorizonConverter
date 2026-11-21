# 第三方组件与许可

本仓库包含自己编写的 C# 代码，以及若干第三方组件（NuGet 包、移植的算法、参考的规范实现）。
下面按组件列出许可与义务。**发布或再分发时请保留本文件与 `docs/licenses/` 下的许可证全文。**

---

## 一、运行时依赖（捆绑进 CLI / GUI / APK）

| 组件 | 版本 | 许可 | 版权 | 用途 |
| --- | --- | --- | --- | --- |
| [SixLabors.ImageSharp](https://github.com/SixLabors/ImageSharp) | 3.1.5 | Six Labors Split License 1.0 | © Six Labors | 读取 PSD / TIFF / HDR / QOI 等图片格式 |
| [NLayer](https://github.com/naudio/NLayer) | 3.0.0 | MIT | © 2011 Mark Heath | MP3 解码 |
| [NVorbis](https://github.com/NVorbis/NVorbis) | 0.10.5 | MIT | © 2020 Andrew Ward | OGG Vorbis 解码 |
| [FlacBox](https://flacbox.codeplex.com/) | 1.0.1 | 上游未附许可证文件（见下方说明） | © 原作者 | FLAC 解码 |

- **Six Labors Split License 1.0**：开源 / 非商业用途免费；商业用途需向 Six Labors 取得商业许可。
  全文见 [`docs/licenses/SixLabors.Split.License-1.0.txt`](docs/licenses/SixLabors.Split.License-1.0.txt)。
  如果你要把本项目用于商业产品，请先把 ImageSharp 替换掉（或购买其商业许可）。
- **NLayer**：全文见 [`docs/licenses/NLayer-MIT.txt`](docs/licenses/NLayer-MIT.txt)。
- **NVorbis**：全文见 [`docs/licenses/NVorbis-MIT.txt`](docs/licenses/NVorbis-MIT.txt)。
- **FlacBox**：NuGet 包内没有 `LICENSE` 文件，上游 CodePlex 页面也已下线，无法确认授权条款。
  本项目仅用它解码 FLAC。**若要商用或严格合规，建议换成有明确许可的 FLAC 解码器**（例如 libFLAC 的托管绑定），
  或直接移除 FLAC 支持。

## 二、自行移植 / 参考的算法

| 组件 | 许可 | 版权 | 在本项目中的位置 |
| --- | --- | --- | --- |
| [LZHAM codec](https://github.com/richgel999/lzham_codec) | **Public Domain**（2020-09-15 明确放弃版权） | © Rich Geldreich 等 | `src/AeroflyConv.Core/Lzham/*`（纯 C# 移植的解码器与编码器） |
| [ARM astc-encoder](https://github.com/ARM-software/astc-encoder) | Apache-2.0 | © ARM Limited | `src/AeroflyConv.Core/Codecs/AstcEncoder.cs`、`AstcTables.cs`（块头位布局与权重量化表按该实现对齐） |
| [AeroflyFS-Native-Converter](https://github.com/RishiK-06/AeroflyFS-Native-Converter) | MIT | © Rishi K | 文件格式（TTX / TSB / TTC / TM）解析与转换行为的参考实现 |

- **LZHAM** 为公有领域，无署名义务；本项目仍在此致谢。全文见
  [`docs/licenses/LZHAM-public-domain.txt`](docs/licenses/LZHAM-public-domain.txt)。
- **ARM astc-encoder** 为 Apache-2.0：再分发时需保留许可证与 NOTICE，全文见
  [`docs/licenses/astc-encoder-Apache-2.0.txt`](docs/licenses/astc-encoder-Apache-2.0.txt)。
  本项目未包含 astc-encoder 的源码，仅按其文档与实现对齐了块头位布局、ISE 编码与量化表；
  如需严格合规，可将其 `LICENSE.txt` 一并随发行包分发（已列在上面的路径）。
- **AeroflyFS-Native-Converter** 为 MIT，见仓库根目录 `LICENSE` 中的声明。

## 三、未包含的内容（重要）

本仓库**不包含**任何 Aerofly FS 的游戏资源（`.ttx` / `.tmb` / `.tsb` / `.ttc` / `.tm*` 及涂装贴图），
也不包含 `.afsenc` 的破解工具或密钥。测试用的样本文件请自备，且不要提交到公开仓库。

## 四、本项目自身

自己编写的代码（转换引擎、LZHAM 的 C# 移植、ASTC 编解码、GUI、Android 应用、文档）以 MIT 发布，
见根目录 `LICENSE`。
