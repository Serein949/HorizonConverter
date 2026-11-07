# 更新日志 / Changelog

`AeroflyConv` ── `RishiK-06/AeroflyFS-Native-Converter` 的 C# 重写版。
版本号对应 `src/AeroflyConv.Core/ProductInfo.cs` 里的 `Version` 常量。

---

## [1.21] - 2026-11-08

### 修复

- **LZHAM 压缩此前形同虚设**：旧版写进 `.ttx` 的 LZHAM 流是「合法但不压缩」的 raw block，
  文件体积≈未压缩。本版换成真正的 LZ 编码器（匹配查找 + 自适应算术编码 + Huffman 前缀表 +
  raw 回退）。

  | 数据 | 1.2 | 1.21 | 官方 C++ 参考 |
  |---|---|---|---|
  | 2048² DXT1 payload（2,796,528 B） | 2,796,592 B（无压缩） | **324,706 B（8.61×）** | 305,629 B（9.15×） |

  - 产出流与游戏原文件格式一致（`6E CD` 头，dict 21 / rate 20），游戏可直接读取；
  - 用官方 C++ `lzham_codec`、本项目 C# 解码器、社区 `tmcompress.wasm` 三套解码器交叉验证，
    解出的 payload 逐字节相同；
  - 随机/不可压数据自动回退 raw block，不会膨胀；
  - 压缩与非压缩两版转回 PNG 后像素完全一致（SHA256 相同）。
  - 速度：解码 99 MB/s，编码 5–7 MB/s。

### 新增

- **ASTC 编码器（纯托管 C#）**：`PNG → TTX` 可写出手游读取的 ASTC 贴图。
  - 支持 4×4 / 5×5 / 6×6 / 8×8 块；
  - 与官方解码器 `texture2ddecoder` 交叉验证：所有块逐字节一致、无非法块；
  - 画质 41–51 dB（2048² fuselage 41.99 dB）；
  - 速度：2048² 约 30 s（单分区搜索的代价，明显慢于 DXT1）。
- **Public 版手机 App**：与 Alpha 版可共存。
  - Alpha `com.aeroflyconv.app`：全部 8 种模式；
  - Public `com.aeroflyconv.converter`：只保留 PNG→TTX、WAV→TSB。
- 命令行 `--version`，桌面 GUI 标题栏与 exe 版本信息统一为 `Aerofly Converter 1.21`。
- 逐文件处理状态输出、新图标。

### 变更

- 桌面与手机版本号统一为 **1.21**。
- 桌面 exe 用本地证书 `CN=AeroflyConv Local Publishing` 签名，配合仓库内
  `签名-去除安全提示.cmd` 可消掉首次运行的 SmartScreen 提示。

### 已知问题

- ASTC 编码速度慢（2048² 约 30 s），必要时可只对手机版贴图启用。
- `tests/vectors` 参考向量清单尚未重建，`tools/Verify` 暂时跑不起来。

---

## [1.2] - 2026-11-01

### 新增

- **Android 应用**（.NET for Android）：可在手机上直接转换 TTX / TSB / TM 等格式。
- 纯 C# ASTC LDR 解码器，手机贴图支持已在真机验证。
- 中文界面、格式与尺寸选项、自定义输出文件夹、转换完「打开 / 分享」。
- ETC2 编码启用，并修正了只读 slot 格式的写入问题。
- 新增 `docs/formats.md`（格式说明）。

---

## [1.1] - 2026-10-31

### 新增

- 输出目录支持（GUI 的 Output 选择器、CLI 的 `-d/--outdir`），无写权限时自动回退。
- 按扩展名自动选择转换模式。
- 贴图默认翻转（Aerofly 自下而上存储）；补充 alpha 透明度说明；支持用户目录里
  XOR 混淆的涂装 TTX。
- 显式指定输出贴图尺寸（GUI 的 Size 选择器、CLI 的 `--size`），在 slot 自适应之上再做缩放。
- `PNG → TTX`：沿用原贴图的尺寸与格式，强制 2 的幂，逐文件翻转默认开启，警告回传 GUI 日志。
- 自签名代码签名（`tools/sign-app.ps1` + 一键 `.cmd`），并接入 `build.ps1`。
- Linux arm64（Termux / Android）CLI 构建与打包说明。

### 修复

- GUI 不再依赖可写的 `%TEMP%`（WinForms 激活上下文清单），并加入致命错误对话框与日志。
- 检测并警告 Nahimic / A-Volute 注入导致的 `0xC0000005` 崩溃，附排查脚本。

---

## [1.0] - 2026-10-31

### 新增

- 首个版本：`RishiK-06/AeroflyFS-Native-Converter` 的 C# 重写。
  - 纯托管 C# 的 LZHAM 编解码，不再依赖 `wasmtime` / `pylzham`；
  - 托管 SIMD 的 BC1/BC2/BC3/BC4/BC5/ETC1/ETC2 纹理解码；
  - SIMD 块编码器（BC1 画质 40.9 dB，原版朴素实现 38.0 dB）；
  - 自写 PNG 编码器（逐行自适应滤波 + .NET zlib），比 Pillow 快 4–5 倍；
  - `Parallel.ForEach` 并行批处理，默认线程数 = CPU 线程数；
  - 单文件自包含发布，目标机器无需安装 .NET。

### 性能（首批实测，24 个真实 `.ttx`，66.4 MB）

| 场景 | Python 原版 | C# 单线程 | C# 8 线程 |
|---|---|---|---|
| TTX → PNG（24 个文件） | 183.2 s | 14.6 s（12.5×） | 4.7 s（39×） |
| 单个 4096² DXT1 TTX → PNG | 12–16 s | ≈1.0 s | — |
| PNG → TTX（1024² DXT1，含 mip 链 + LZHAM） | 11.1 s | 0.37 s（30×） | — |
