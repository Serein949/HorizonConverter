# 更新日志 / Changelog

`AeroflyConv` ── `RishiK-06/AeroflyFS-Native-Converter` 的 C# 重写版。
版本号对应 `src/AeroflyConv.Core/ProductInfo.cs` 里的 `Version` 常量。

---

## [1.22] - 2026-11-15

### 新增

- **手机端输入格式放宽**：「PNG → TTX」改名「图片 → TTX」，直接支持 JPG / JPEG / BMP / GIF / TGA / WebP /
  **PSD** / TIFF / HDR / PBM / QOI，格式按文件内容识别；选择器不再按 MIME 过滤，任何文件都能选进来。
- **新增「音频 → TSB」**：MP3 / FLAC / OGG / WAV 直接打包成 Aerofly 音效包（`AudioDecoder` → WAV → `TsbFile`）。
  `WAV → TSB` 并入该模式。
- **新增「TTX → TTX」**：手机端解码后按新格式重编码，支持 DXT1 / DXT5 / ETC2 / R8 / RGBA 无损 / ASTC，
  可改尺寸并保留 mip 链与 LZHAM 压缩；同名输入不覆盖原文件（写成 `*.converted.ttx`）。
  实测 2048² ASTC 6x6 → DXT5 只用 **5 s**，输出 404,000 B（LZHAM 13.8×）。
- **新增「RGBA 无损」输出**：`type_rgba`（32 位、不经块压缩）。2048² PNG 加 LZHAM 为 **1.65 s / 934,616 B**，
  关闭垂直翻转时回读与源图**逐像素一致**；容器不压缩时 0.43 s / 22,369,992 B（对照 2048² → DXT1 为 1.34 s / 324,992 B）。
- 桌面 CLI 同步新增 `-m audio2tsb`、`-m ttx2ttx`，并支持上述新图片格式。

### 变更

- **模式栏去重**：`PNG → TTX` 并入「图片 → TTX」，`WAV → TSB` 并入「音频 → TSB」；
  Public 版只保留 **图片 → TTX / 音频 → TSB / TTX → TTX** 三个模式。
- 与当前模式不匹配的文件不再报错中断，日志显示「跳过 …」。

### 版本

- 版本号统一到 **1.22**：Alpha `versionCode 12`、Public `versionCode 9`、桌面 1.22（桌面命名不区分 Alpha / Public）。

### 验证

- 手机端 1024² 三个图片（PNG / JPG / BMP）→ TTX：4.7 s，输出 `type_rgba` mips=11。
- 音频 → TSB：回读采样与源 WAV 完全一致。

---

## [1.21.6] - 2026-11-14

### 修复

- **ASTC 贴图在游戏里出现黑块 / 棋盘噪声**（机库里模型整体发黑）。
  原因：ASTC 的 16 字节块头有严格位布局（11 位 `block_mode`、分区数、4 位 CEM、从第 17 位开始的 ISE 端点、
  写在块尾的权重），旧编码器用的是自家简化布局，自研解码器用同一套布局去解，所以「回转 PSNR 42 dB、
  看着完全正常」，交给 GPU 就全乱——软件解码器宽容，硬件不宽容。
  已按 ARM 官方 astcenc 的 `symbolic_to_physical` / `decode_block_mode_2d` 重写
  `src/AeroflyConv.Core/Codecs/AstcEncoder.cs`（公开 API 不变）：枚举全部 2048 个 block mode 选最优、
  端点用 `encode_ise` 纯比特编码、权重按 astcenc 方式从块尾写入、常量块改用规范的 void-extent 形式。
- **选 ASTC 格式时闪退**：`SetFormatVisibility` 用定长数组索引格式药丸，选中 ASTC（索引 5/6）越界抛
  `IndexOutOfRangeException`；已改为越界即视为可用。

### 验证

- 真机 Adreno 740 用 `glCompressedTexImage2D` 上传、`glReadPixels` 取回，与软件解码逐像素比对：
  4x4 67.44 dB、5x5 67.16 dB、6x6 68.56 dB、8x8 68.81 dB，最差差值 1，**坏块 0**。
- 顺带修了验证工具两个漏洞：GPU 对照 Activity 之前把贴图拉伸铺满屏幕再读一个角落（读数本身就错），
  改为 1:1 视口；`tools/AstcEncCheck` 在 Python 交叉校验不可用时不再打印「通过」。
- 实测：手机端 512×512 ASTC 6x6 十级 mip → `type_rgb_astc_6x6` 62,392 B（LZHAM 158,808 → 62,392，2.5×）；
  桌面 2048² ASTC 6x6 约 7.2 s / 41.99 dB，5x5 约 7.1 s / 47.21 dB。

### 打包

- 桌面 CLI/GUI 用带版本元数据的发布配置重新打包（`afc86a5`），桌面产品名去掉「Alpha」后缀。

---

## [1.21.5] - 2026-11-08

### 变更

- **手机界面回退到 1.2 的样式**：撤掉 1.21.3 的玻璃拟态与 1.21.4 的流动极光背景，恢复深海军蓝底 + 青色强调色 ——
  底色 `#0B0E14`、卡片 `#151A22`（18dp 圆角、1dp 描边）、强调色 `#00B8FF`。
  1.21.3 / 1.21.4 的问题是整体偏灰、开关的开/关状态看不出来。
- 开关状态改为明显可见；修复药丸选择器的滚动问题。
- 界面上显示的版本号改为读取 `ProductInfo`，不再硬编码。

---

## [1.21.4] - 2026-11-08

### 新增

- 手机端动态极光背景：缓慢流动的彩色光团、扫过的光束、两个漂移光点、暗角；色相每 48 秒转一圈
  （青蓝 → 紫 → 品红 → 琥珀 → 翠绿）。

### 修复

- `AuroraView` 改为在代码里创建，不再依赖布局 XML；绘制加保护；补充崩溃日志；并重新打包 APK。

> 注：闪退修复后重打的 APK 与首版同名同版本号，但体积不同（首版 12,126,634 B，修复版 12,130,730 B），
> 安装时请以 `dist\v1.21.4\android\` 下的最终文件为准。

---

## [1.21.3] - 2026-11-08

### 变更

- 手机端界面改版：改用 iOS「流体玻璃」设计语言 —— 三张卡片布局、分段式模式切换、可折叠日志，
  功能一个没减。

> 本版配色偏灰、开关状态不易辨识，已在 1.21.5 撤回到 1.2 的扁平风格。

---

## [1.21.2] - 2026-11-08

### 新增

- **ASTC 编码质量档位**：`--astc-quality fast|standard|high`，手机 App 里合成一行「编码质量」，默认标准、选择会被记住。

  | 档位 | 2048² 耗时 | 输出体积 | PSNR | 说明 |
  |---|---|---|---|---|
  | `fast` | 2.5 s | 494.9 KB | 41.66 dB | 只精修 3 个配置、1 轮迭代 |
  | `standard` | 5.6 s | 495.7 KB | 41.95 dB | 默认档，与 1.21.1 输出字节一致 |
  | `high` | 12.0 s | 495.9 KB | 41.99 dB | 精修 16 个配置、权重窗口 ±3 |

  小图固定开销占比大，差距更小：512² 快速 1.0 / 标准 1.8 / 高质量 2.1 s；1024² 为 1.6 / 2.6 / 4.9 s。

### 变更

- 版本号 1.21.2；Alpha versionCode 7、Public versionCode 4，两个 App 仍可共存（图标 `α` 与 `P` 小徽章）。

### 已知问题

- 本版手机端只做了编译验证，发布时测试机未连接，质量档位未在真机上跑过；桌面 CLI 三档数值为实测。
- 桌面 GUI 只跟随版本号，质量档位在 GUI 里暂不可选，需用 CLI。

---

## [1.21.1] - 2026-11-08

### 变更

- **ASTC 编码器提速 5 倍**：2048² 从 31.3 s 降到 **6.0 s**，做法是块搜索改 `Parallel.For` 全核并行、砍掉多余的分区试探。
- 版本号 1.21.1；桌面 exe 重新发布（cli 36.75 MB / gui 50.23 MB，与发布说明中的体积一致）。
- README 补上 1024² / 512² 的 ASTC 计时。

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
  - 速度：2048² 约 30 s（单分区搜索的代价，明显慢于 DXT1；1.21.1 起已优化到 6 s，见上）。
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

- ASTC 编码慢（2048² 约 30 s，比 DXT1 慢两个数量级），必要时可只对手机版贴图启用。*（1.21.1 起 6 s，1.21.2 起可选档位）*
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
