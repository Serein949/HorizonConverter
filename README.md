# AeroflyConv — 快得多的 Aerofly FS 转换器（C# / .NET 10）

[`RishiK-06/AeroflyFS-Native-Converter`](https://github.com/RishiK-06/AeroflyFS-Native-Converter) 的 C# 重写版：
功能一一对照，但整条处理管线都是原生代码 —— 纯托管 C# 的 LZHAM 编解码器（**真压缩，8.61×**）、
SIMD 的 BC1/BC3/ETC2 纹理编解码、**ASTC 编解码**（手机版贴图）、自写的高吞吐 PNG 编码器、
多文件并行批处理。

![GUI](docs/screenshot.png)

## 为什么快

| 环节 | 原版（Python + PySide6） | 本版（C# / .NET 10） |
|---|---|---|
| LZHAM | `wasmtime` 跑 `tmcompress.wasm`，压缩靠 `pylzham` | 纯 C# 移植的编解码器：解码 99 MB/s、编码 5–7 MB/s（无原生依赖） |
| 纹理解码 | `texture2ddecoder` + 逐像素 Python 循环补 RGBA | 托管 SIMD BC1/BC2/BC3/BC4/BC5/ETC1/ETC2 |
| 纹理编码 | 逐块纯 Python BC1/BC3 | SIMD 块编码器（质量高于原版朴素实现：BC1 40.9 dB vs 38.0 dB） |
| PNG 写出 | Pillow（Generic 管线） | 自写 PNG 编码器 + 逐行自适应滤波 + .NET zlib，快 4–5 倍 |
| 批处理 | 单线程 | `Parallel.ForEach`，默认 = CPU 线程数 |
| 运行环境 | Python 3 + 6 个原生依赖 | 单个自包含 exe，目标机器无需装运行时 |

### 实测（同一批 24 个真实游戏 `.ttx`，共 66.4 MB，4096² / 2048² 混合）

| 场景 | Python 原版 | C# 单线程 | C# 8 线程 |
|---|---|---|---|
| TTX → PNG（24 个文件） | **183.2 s** (0.13 文件/s) | **14.6 s**（12.5×） | **4.7 s**（39×） |
| 单个 4096×4096 DXT1 TTX → PNG | 12–16 s | ≈ 1.0 s | — |
| PNG → TTX（1024² DXT1，含 mip 链 + LZHAM） | 11.1 s | 0.64 s（17×） | — |

LZHAM 本体：解码 **99 MB/s**、编码 **5–7 MB/s**；压缩率 **8.61×**（官方 C++ 参考实现 9.15×），
流格式与发行版逐字段一致（`6E CD` + dict 21 / rate 20），官方 C++、本项目 C# 与
`tmcompress.wasm` 三套解码器结果一致。

---

## 1. 怎么跑

### 免安装（推荐）

```
dist\gui\AeroflyConv.exe          # 图形界面
dist\cli\AeroflyConv.exe --help   # 命令行
```

自包含单文件（GUI ≈ 50 MB / CLI ≈ 37 MB），目标机器不需要 .NET。

### 自己编译

```powershell
cd AeroflyConv
.\build.ps1                       # 输出 .\dist\cli 与 .\dist\gui（win-x64 自包含单文件）
# 若被 PowerShell 执行策略拦住：
powershell -ExecutionPolicy Bypass -File .\build.ps1

dotnet run --project src/AeroflyConv.Gui
dotnet run --project src/AeroflyConv.Cli -- "D:\liveries" -m png2ttx --flip
```

要求 .NET 10 SDK。

---

## 2. 命令行

```powershell
# 按扩展名自动识别（同原版）
AeroflyConv "C:\Aerofly FS 4\aircraft" -j 8

# 纹理
AeroflyConv livery.ttx                        # -> livery.png
AeroflyConv livery.png -m png2ttx --flip      # -> livery.ttx（dxt_auto：有透明用 DXT5，否则 DXT1）
AeroflyConv livery.png -m png2ttx -f dxt1     # dxt1 / dxt5 / type_rgba / type_r / etc2 / type_rgba_etc2

# 场景贴图
AeroflyConv tile.ttc                          # -> tile.png（默认上下翻转）
AeroflyConv tile.png -m png2ttc --ttc-format dxt1

# 声音
AeroflyConv engine.tsb                        # -> engine.wav
AeroflyConv engine.wav                        # -> engine.tsb
AeroflyConv song.flac                         # -> song.wav（也支持 .mp3 / .ogg）

# TM 容器 -> 通用文本树
AeroflyConv airport.tmb                       # -> airport.tmb.txt

# 只看信息
AeroflyConv some.ttx --info
```

参数：`-m/--mode`、`-o/--output`、`-f/--format`、`--ttc-format`、`--flip/--no-flip`、
`--no-mipmaps`、`--no-compress`、`--png-quality fast|balanced|best`、`--zoom N`、
`--no-overwrite`、`-j/--jobs N`、`--info`。

---

## 3. GUI

界面风格参照同机的 `AirportStudioNative`（Direct2D 玻璃拟态）：近黑藏青底色、两道冷白色
径向光晕、1px 白色 10% 描边的半透明卡片、青色 `rgb(0,184,255)` 强调色与绿色成功色，
左侧分类侧边栏 + 选中项青色光条。全部控件自绘（`GlassCard` / `GlassButton` / `GlassSelect` /
`GlowProgressBar` / `NavList`），并启用 WinForms 原生深色模式 + Windows 11 深色标题栏/圆角。

功能：拖放文件或文件夹、按类别分组的模式列表、格式下拉框、线程数、翻转/多级 mip/压缩开关、
进度条、实时日志、"打开输出目录"。

---

## 4. 实现说明

* **LZHAM**（`src/AeroflyConv.Core/Lzham`）：从官方 `lzham_codec` C++ 源码移植的自适应算术
  编解码器（sync/raw/comp/EOF 四种 block、全部符号模型、Huffman 前缀表、匹配查找、raw 回退）。
  解码 99 MB/s、编码 5–7 MB/s；2048² DXT1 贴图 2,796,528 → 324,706 字节（8.61×，官方参考
  实现 9.15×）。产出的流被三套解码器读回一致：官方 C++、本项目 C#、社区工具用的
  `tmcompress.wasm`。
* **纹理编解码**（`Codecs/`）：BC1/BC2/BC3/BC4/BC5 与 ETC1/ETC2/EAC 解码、BC1/BC3、
  ETC2 RGB/RGBA 编码，以及 **ASTC LDR 编解码**（4×4/5×5/6×6/8×8），全部托管实现；解码与
  `texture2ddecoder` 逐字节一致，ASTC 编码产出的块同样两套解码器逐字节一致。
* **容器**（`Tm/`、`Textures/`）：TTX/TTC/TSB/TM 的 chunk 树、mip 链、`compress_file`
  包装、CRC32、tmcompress 头，按原版语义重写（并兼容 IPACS 的 `string8u` 变体）。
* **PNG**：读用 ImageSharp，写用自写编码器（RGB/RGBA、逐行 MSAD 自适应滤波）。
* **音频**：NLayer（MP3）、NVorbis（OGG）、FlacBox（FLAC），全部纯托管。
* **并行**：文件级并行；单文件内部按顺序处理。

> 贴图格式（DXT1 / DXT5 / ETC2 / R8 / BC5 / ASTC）的区别、体积、画质实测和选择建议，
> 见 [`docs/formats.md`](docs/formats.md)。

---

## 5. 验证

`tests/vectors` 里是从**真实游戏文件**生成并与原版 Python 工具对照过的参考数据：

* 24 个真实容器（.ttx / .toc / .tmb / .tsc / .tsl / .wad / .tsb）的 LZHAM 解压结果哈希
* 纹理解码后的 RGBA 哈希（含 DXT1/DXT5/BC5/ETC2）
* TM→文本树的完整文本哈希（含 55 MB 的巨大 .tmb）
* TSB→WAV 字节哈希
* 原版实现（真 `pylzham` 压缩流）编码出的 TTX，用于验证 C# 解码器能吃"真压缩块"
* C# 编码出的 TTX 分别用官方 C++ `lzham_tool`、本项目解码器、以及社区工具用的
  `tmcompress.wasm` 反向验证，解码结果逐字节一致；压缩率与官方参考实现相差 6%

```powershell
dotnet run --project tools/Verify       # 端到端对照（需要 tests/vectors 参考数据）
dotnet run --project tools/LzhamCheck -- <vectorsDir>   # LZHAM 解压向量 + 编码回环 + 吞吐
dotnet run --project tools/LzhamEncCheck -- tests/corpus # LZHAM 编码器差分（官方工具对照）
dotnet run --project tools/AstcEncCheck  # ASTC 编码 PSNR + 双解码器一致性
dotnet run --project tools/CodecCheck   # 块压缩编解码 vs texture2ddecoder / etcpak
```

> `tests/vectors` 未随仓库提交（几十 MB 的真实游戏文件）。重新生成后可跑 `tools/Verify` 与
> 完整的 `tools/LzhamCheck`。

---

## 6. 已知限制（与原版的差异）

* **ASTC**（`type_rgb_astc_*` / `type_rgba_astc_*`，Aerofly 移动版用）可以解码，也可以编码出
  4×4/5×5/6×6/8×8 的 LDR 块（`--format astc6x6` / `astc5x5`）。编码器是单分区、8 位端点实现：
  真实贴图 41–51 dB，通道独立的照片类内容约 29–30 dB（官方 astcenc quality 60 更高，但
  需要多分区搜索）。
* 游戏自带的 `scenery/images/map_*.ttc` 切片（头部格式标记 `0x1234567x`）是未公开的另一种
  变体，原版 Python 工具同样无法读取；本版给出明确提示。转换器自己生成的 TTC 正常工作。
* **BC5 法线贴图**：本版按法线贴图规范重建 Z 通道（正确）；原版总是用零通道推导 Z，
  所以两者蓝色通道不同（红/绿通道逐字节一致）。这是有意修正。
* **ASTC 编码速度**：2048² 贴图约 6 s（1.21.1 起；1.21 约 30 s），仍比 DXT1 慢一个数量级；
  手机上更慢。只影响 PNG→ASTC，不影响解码与其他格式。
* 输入图片支持 PNG/JPG/BMP/GIF/TGA/WebP（ImageSharp）。

---

## 7. 目录结构

```
src/AeroflyConv.Core/      格式实现：Tm/ Textures/ Codecs/ Lzham/ Media/ Conversion/
src/AeroflyConv.Cli/       命令行入口（输出 AeroflyConv.exe）
src/AeroflyConv.Gui/       WinForms 界面（Ui/ 自绘控件）
tools/LzhamCheck/          LZHAM 自检
tools/CodecCheck/          块压缩自检
tools/Verify/              端到端对照验证
tests/vectors/             参考数据（约 14 MB）
refharness/                生成参考数据的 Python 脚本（需要原版仓库）
docs/screenshot.png        GUI 截图
```

---

## 8. 常见问题：运行一会儿后闪退（NahimicOSD.dll）

Windows 事件日志里如果看到：

```
Faulting application name: AeroflyConv.exe
Faulting module name: NahimicOSD.dll, version: 2.2.25.0
Exception code: 0xc0000005
```

这不是本程序的问题：Dell / Alienware 机器上的 **Alienware Sound Center（Nahimic）** 会把
`NahimicOSD.dll` 注入到每个进程，而这个模块有已知的崩溃缺陷（原生 0xC0000005）。

处理办法（任选其一）：

1. 双击程序目录里的 `NahimicOSD-停止.cmd`（会请求管理员权限），再启动 AeroflyConv；
   需要还原时运行 `NahimicOSD-恢复.cmd`。
2. `services.msc` → 找到 `NahimicService` → 停止，并把启动类型改成「禁用」（永久生效）。
3. 在 Alienware Sound Center / Nahimic 里关闭 OSD / 声音追踪类的叠层功能。
4. 只用命令行版（`dist\cli\AeroflyConv.exe`）批处理，它不开窗口，更不容易被 OSD 碰到。

程序启动时如果检测到该 DLL 已注入，会在日志里给出红色警告并弹窗提示。

---

## 9. 常见问题：`Access to the path ... is denied`

转换结果默认写在源文件旁边。游戏安装目录（`...\SteamLibrary\steamapps\common\Aerofly FS 4 ...`）
一般不允许普通用户写入，所以会报权限错误。三种解决办法：

1. **GUI**：`Options → Output` 选一个可写目录（可写目录里的文件夹结构会按输入镜像），
   右侧 `?` 恢复"写在源文件旁边"。
2. **CLI**：`-d/--outdir`

   ```powershell
   AeroflyConv "E:\SteamLibrary\steamapps\common\Aerofly FS 4 Flight Simulator\aircraft\a350_1000" -d D:\AeroflyPNG -j 16
   ```

3. **什么都不做**：程序会自动改写到 `Documents\AeroflyConv-out`（若也不可写则依次尝试
   `%LOCALAPPDATA%\AeroflyConv\out`、临时目录、exe 同目录 `out\`），日志里会注明实际位置。

另外 "auto" 模式在扫描文件夹时只处理已知的输入扩展名（`.ttx .tsb .wav .mp3 .flac .ogg .ttc
.toc .tsc .wad .tmb .tsl .zip .tme` 和图片），不会再对 `.json`、`.cfg` 之类的文件报"无法识别"。

---

## 10. 贴图方向与"黑色区域"

* **上下颠倒**：Aerofly 的 TTX 贴图按自下而上的顺序存储。导出 PNG 时默认**已经翻转**，
  所以打开就是正立的（GUI 里 `Flip vertically (Aerofly stores textures bottom-up)` 默认勾选，
  命令行可用 `--no-flip` 关闭）。写回 TTX 时也默认翻转，因此"导出→编辑→导入"能对上。
* **某个区域显示为纯黑**：那是 **alpha = 0 的透明像素**，照片查看器会把透明画成黑色
  （A350 的 276 张 RGBA 输出里有 183 张底部带完全透明，属于贴图本身的内容）。
  用 Photoshop / GIMP / 支持透明通道的查看器打开就是透明的。转换日志里现在会提示
  "x% of the pixels are transparent (alpha=0)"。

## 11. 用户目录里的涂装 TTX（原版读不了的那种）

`Documents\Aerofly FS 4\aircraft\...` 里的自制涂装 `.ttx` 用的是另一种外层头：
前 `0x40` 字节与 `0x5A` 异或，外层 magic 为 `a5 a4 24 c7 b7 3c eb b7`。
原版 Python 工具会直接报 "not a TTX texture container"，本版已识别并解出
（解完就是普通的 LZHAM `compress_file` 容器）。实测把整个 `Documents\Aerofly FS 4\aircraft`
目录（含南航 A330 等涂装）25 个文件 3.8 秒转换完成。

---

## 12. 贴图尺寸 / 覆盖原贴图（重要）

游戏里的每个贴图"槽位"都有固定尺寸和格式（A350 机身 `ext01_fuselage_color.ttx` 是
**2048×2048 / DXT1 / 12 级 mip**）。如果写进去的 TTX 尺寸不对（例如 6136×6132、
非 2 次幂、13 级 mip），游戏采样就会错位/加载失败——表现为机身大面积发黑、出现补丁、
看起来"上下颠倒"。

v1.1 自动处理：

* **覆盖已存在的 .ttx 时**：读取该贴图原有的宽高和 GPU 格式，把源图缩放/转成一致的
  （`--no-fit` 可关闭）。
* **目标不存在时**：非 2 次幂的图片自动缩到最近的 2 次幂（如 6136×6132 → 4096×4096）。
* 缩放用面积平均（缩小）/ 双线性（放大），GIMP/PS 之外不需要自己先改尺寸。

实测：把原贴图导出成 PNG 再原样写回，尺寸/格式/mip 数与原文件一致，往返 PSNR 46.8 dB。
（文件体积会比 IPACS 原版大，因为 LZHAM 写的是 raw block，不影响读取。）

### 输出尺寸怎么选

覆盖已有贴图时默认 **Match original**（和原槽位一致，最安全）。想要更清晰可以选更大尺寸：

| 选项 | 结果（源图 6136×6132） | 说明 |
|---|---|---|
| Match original | 2048×2048 / 12 mips，2.7 MB | 与原版槽位完全一致 |
| 4096 px | 4096×4096 / 13 mips，10.7 MB | 比 2048 保留更多细节，推荐 |
| 2048 px | 2048×2048 | 强制降采样 |
| 8192 px | 8192×8192 / 14 mips，42.7 MB | 已超过源图分辨率，只是放大，不增加细节 |
| Source (max PO2) | 4096×4096 | 取不超过源图的 2 次幂 |

命令行：`--size 4096`。显存/加载时间随面积线性增长：40962 约为 20482 的 4 倍，
81922 又是 40962 的 4 倍（DXT1 单张约 11 MB → 43 MB，含 mip）。

---

## 13. 去掉「Windows 已保护你的电脑 / 仍要运行」

这是 SmartScreen 对**未签名程序**的提示。本仓库提供一个一键处理脚本（当前用户证书，不需要管理员）：

```powershell
# 先关闭 AeroflyConv（签名需要写文件）
powershell -NoProfile -ExecutionPolicy Bypass -File tools/sign-app.ps1
# 或在发布目录里双击：签名-去除安全提示.cmd
```

它会：

1. 创建自签代码签名证书 `CN=AeroflyConv Local Publishing`（当前用户，5 年）；
2. 把该证书导入 `当前用户\受信任的根证书颁发机构` 与 `受信任的发布者`；
3. 给所有 `AeroflyConv.exe` 签名（SHA-256）。

之后双击 exe 不再弹"仍要运行"。`build.ps1` 发布会自动调用它（签名证书存在时）。

撤销（删除信任与证书）：

```powershell
certutil -delstore -user Root 583EEC3674E239898741C9809B2EF06E96F7E748
certutil -delstore -user TrustedPublisher 583EEC3674E239898741C9809B2EF06E96F7E748
Remove-Item Cert:\CurrentUser\My\583EEC3674E239898741C9809B2EF06E96F7E748
```

---

## 14. 安卓 / 其他平台

**核心库是可移植的**：`AeroflyConv.Core` 6501 行、零平台 API（没有 System.Drawing / DllImport /
Win32），依赖的 ImageSharp / NLayer / NVorbis 也都是跨平台的。

已经验证的落地方式：

| 方式 | 状态 | 命令 |
|---|---|---|
| Linux / Android(Termux) 命令行 | ? 已构建可用 | `dotnet publish -r linux-arm64 --self-contained` → `dist/AeroflyConv-android-termux-arm64.zip` |
| .NET for Android 原生小 App | 待做（需 Android SDK） | 复用 Core，SAF 文件选择 + 进度 + 保存到 Download |
| MAUI 跨平台 GUI | 待做（需 `maui-android` workload） | 界面需重写 |

在 Android 上用之前要知道的两件事：

1. **存储限制**：Android 11+ 里 `Android/data/<包名>/` 对其他 App（Termux 也算）不可读。
   如果 Aerofly FS Global 的内容在这个目录，必须先 root / Shizuku，或把文件复制到
   `Download` 再用本工具处理。
2. **ASTC**：移动版用 ASTC 贴图，本项目的 ASTC 解码与编码都已实现（4×4/5×5/6×6/8×8），
   不再需要额外的原生依赖。

---

## 15. 安卓 App（实测可用）+ 移动版 ASTC 支持

### 15.1 Android 应用

`src/AeroflyConv.Android` 是一个 .NET for Android 原生应用（不是 MAUI，界面用 XML 布局），
APK 在 `dist/v1.21.4/android/AeroflyConverter-Alpha-1.21.4.apk`（minSdk 24 / target 36）；
精简版是 `AeroflyConverter-Public-1.21.4.apk`（只保留 PNG→TTX、WAV→TSB，可与 Alpha 共存）。

安装：`adb install -r AeroflyConverter-Alpha-1.21.4.apk`，或拷到手机后用文件管理器点击安装。

**界面（1.21.3 起为 iOS"流体玻璃"风格，全中文）**：

* 动态极光背景（彩色光团流动 + 扫过的光束 + 漂移光点，色相 48 秒转一圈），内容放在 3 张半透明玻璃卡片里（**文件 / 选项 / 日志**）；
* 顶部**模式**是无卡片的分段控件（玻璃胶囊里滑动选择），当前模式的说明文字直接跟在下面；
* **文件**卡片：`添加文件`（可多选）、`文件夹`（递归扫描）、`清空`，逐个文件可点 ✕ 移除，
  底部一行是**输出位置**（`Download/AeroflyConv` 或自选文件夹，点「更改」）；
* **选项**卡片（按模式自动显示）：格式（自动 / DXT1 / DXT5 / ETC2 / R8 / ASTC 6x6 / ASTC 5x5）、
  贴图尺寸（匹配原尺寸 / 4096 / 2048 / 1024 / 源尺寸）、编码质量（ASTC 专用）、垂直翻转、
  压缩 TTX（LZHAM）、TTC 多级贴图；
* **日志**默认折叠，点标题栏展开（箭头会转），展开状态被记住；转换出现失败时自动展开；
  头部有行数与「复制 / 清空」，每行按级别上色（✓ 成功 / ✕ 失败 / ! 警告 / · 普通）；
* `开始转换` 是渐变胶囊按钮，转换中变成「取消」，下方居中显示进度状态；
* 转换后出现 `打开结果` / `分享`；
* 所有设置存 `SharedPreferences`，下次启动沿用。

也可以在应用私有目录 `Android/data/com.aeroflyconv.app/files/in` 里放文件（方便 adb 批量投放），
用 `am start ... --ez autorun true --es mode png2ttx --es format etc2` 无界面跑批（测试用）。

**实机验证（vivo V2304A / Android 16 / arm64）**：

| 转换 | 结果 |
|---|---|
| TTX (DXT5 1024²) → PNG | 与桌面版**逐像素一致** |
| TTX (BC5 法线 2048²) → PNG | 与桌面版**逐像素一致** |
| TSB → WAV | 与桌面版**逐字节一致** |
| PNG → TTX (DXT1, 256×128, 9 mips) | 与桌面版**逐字节一致** |
| PNG → TTX (ETC2, 512² 与 2048²) | 正常编码，`type_rgb_etc2` |

桌面版与手机版共用同一份 `AeroflyConv.Core`，所以行为完全一致（PNG 压缩字节可能因
zlib 实现不同而略有差异，像素不变）。

### 15.2 ASTC（Aerofly FS Global / 手机版贴图）

手机版的贴图是 `type_rgb_astc_6x6` / `type_rgba_astc_5x5`（容器和 LZHAM 与桌面版相同）。
本版新增**纯 C# 的 ASTC LDR 解码器**（`Codecs/AstcCodec.cs`）：

* 支持 4×4 … 12×12 全部块尺寸、1–4 partition、dual-plane、全部 CEM、void-extent、FP16 端点；
* 用手机 DLC 里的 **85 个真实 ASTC 贴图**做回归（`tests/vectors/astc`），与官方解码器
  `texture2ddecoder` **逐字节一致**；
* 桌面吞吐 ≈ **249 MB/s**（2048×2048 6×6 单张 60–130 ms）；
* `tools/AstcCheck` 可单独跑这套回归。

也就是说：**手机版的贴图现在可以在电脑上转成 PNG，也能转回去了**。

### 15.3 ASTC 编码（1.21 新增）

`Codecs/AstcEncoder.cs` 是纯托管 C# 的 ASTC LDR 编码器，`PNG → TTX` 时可直接写出手游用的
ASTC 贴图（CLI `--format astc6x6` / `astc5x5`，GUI 与手机端格式药丸同步新增）：

* 单分区、8 位端点（CEM8 RGB / CEM12 RGBA），双平面关闭，权重网格在块尺寸内枚举；
* 端点用交替最小二乘拟合，权重用坐标下降取整，评估误差用解码器的精确算式；
* 常数块自动写成 void-extent（无损）；
* 与官方解码器 `texture2ddecoder` 交叉验证：所有块**逐字节一致**，无一非法块；
* 画质：真实贴图 41–51 dB（`callsign_bump` 50.84 dB、`callsign_light` 42.32 dB），
  通道完全独立的照片类内容约 29–30 dB（单分区上限，官方 astcenc 用多分区可达 40 dB+）；
* 速度（1.21.1 优化后，含 mip 链与写盘）：2048² 约 **6 s**（6x6 / 5x5 / 4x4 / 8x8 都是 5.5–6.0 s），
  1024² 约 2.6 s，512² 约 1.2 s。

#### 编码质量档位（1.21.2 新增）

ASTC 编码可以在"速度 / 画质"之间选档，CLI 用 `--astc-quality`，手机端在「编码质量」一行选：

```powershell
AeroflyConv livery.png -f astc6x6 --astc-quality fast      # 快速
AeroflyConv livery.png -f astc6x6 --astc-quality standard  # 标准（默认，与 1.21.1 完全一致）
AeroflyConv livery.png -f astc6x6 --astc-quality high      # 高质量
```

实测（2048² 贴图，16 线程，含 mip 链与写盘）：

| 档位 | 耗时 | 输出体积 | 画质 (PSNR) | 做法 |
| --- | --- | --- | --- | --- |
| 快速 fast | **2.5 s** | 494.9 KB | 41.66 dB | 只精修 3 个配置、1 轮迭代、权重窗口 ±1 |
| 标准 standard | 5.6 s | 495.7 KB | 41.95 dB | 精修 8 个配置、2 轮迭代、权重窗口 ±2 |
| 高质量 high | 12.0 s | 495.9 KB | 41.99 dB | 精修 16 个配置、2 轮迭代、权重窗口 ±3 |

小图差距更小（固定的解码/压缩开销占比大）：512² 快速 1.0 s、标准 1.8 s、高质量 2.1 s；
1024² 快速 1.6 s、标准 2.6 s、高质量 4.9 s。手机端默认「标准」，选项会被记住。

---

## 16. 版本记录

### 1.21.5

* **手机端界面回到 1.2 的样子**：撤掉 1.21.3 / 1.21.4 的玻璃拟态与流动极光背景（`AuroraView.cs`
  已删除），恢复深海军蓝底 `#0B0E14` + 卡片 `#151A22` + 强调色 `#00B8FF`；选中药丸为深青底、
  青色描边、青色文字，与 1.2 一致。
* 开关（垂直翻转 / 压缩 TTX / TTC 多级贴图）补齐选中态配色：轨道打开为 `#00B8FF`、关闭为
  `#252B34`，滑块始终白色；此前两态看不出区别。
* 修复药丸横向滚动过头：切换模式时只滚到"刚好可见"。
* 修复标题与日志里的版本号写死为 `v1.2.0`，改为统一读取 `ProductInfo.Version`。
* 版本号统一到 **1.21.5**（Alpha `versionCode 10`、Public `versionCode 7`）。
### 1.21.4

* **手机端动画背景**：新增 `AuroraView.cs`（纯 Canvas，无第三方库）——6 个彩色光团沿利萨如轨迹
  缓慢游动并用叠加混合，半径会呼吸，色相每 48 秒转一圈；一道光束约 60 秒扫过一圈；两个漂移高光点；
  四周渐晕保证文字对比度。
* 省电处理：仅在可见时绘制（`OnPause` 停 / `OnResume` 续），系统开启"减少动画"时只画静态一帧。
* 玻璃卡片加 `elevation` 投影（8–12dp），层次更清楚。
* 版本号统一到 **1.21.4**（Alpha `versionCode 9`、Public `versionCode 6`）。

### 1.21.3

* **手机端全新界面（iOS"流体玻璃"设计语言）**：页面从 5 张卡片精简为 3 张（文件 / 选项 / 日志），
  模式改成顶部分段控件，输出位置并入文件卡片，日志默认折叠（记住展开状态，失败时自动展开），
  按钮统一成玻璃胶囊，`开始转换` 用渐变胶囊 + 居中状态文字。
* 新增一组玻璃化 drawable（`bg_window` / `bg_glass` / `bg_track` / `bg_pill_on` / `bg_pill_off` /
  `bg_primary` / `bg_ghost` / `bg_ghost_small` / `bg_log` / `bg_file_row`），纯 XML 渐变 + 半透明，
  不依赖 RenderEffect 或第三方库。
* 版本号统一到 **1.21.3**（Alpha `versionCode 8`、Public `versionCode 5`）。

### 1.21.2

* **ASTC 编码质量档位**：新增 `fast` / `standard` / `high` 三档（CLI `--astc-quality`，手机端
  「编码质量」药丸，仅在选择 ASTC 6x6 / 5x5 时出现）。2048² 贴图：快速 **2.5 s** / 41.66 dB，
  标准 5.6 s / 41.95 dB（与 1.21.1 字节一致），高质量 12.0 s / 41.99 dB。
* 版本号统一到 **1.21.2**（Alpha `versionCode 7`、Public `versionCode 4`）。

### 1.21.1

* **ASTC 编码提速 5 倍**：2048² 贴图 31.3 s → **6.0 s**（画质 41.99 dB → 41.95 dB，几乎无损）。
  做法：先给全部配置做廉价打分、只对前 8 名做完整优化；权重层枚举限制在当前位置 ±2 级；
  端点细化与权重更新都加了"超过当前最优即提前退出"的剪枝。
* 版本号统一到 **1.21.1**（Alpha `versionCode 6`、Public `versionCode 3`）。

### 1.21

* **LZHAM 编码器真正实现**（此前写的是 raw block 占位，等于不压缩）：
  2048² DXT1 的 TTX 从 2,796,592 B 降到 **324,992 B（8.61×）**，官方参考实现同数据 9.15×；
  产出流经官方 C++、本项目 C#、`tmcompress.wasm` 三套解码器验证一致。
* **ASTC 编码**落地，手机端 PNG→TTX 可写 ASTC 6x6 / 5x5。
* 桌面与手机版本号统一到 **1.21**；Alpha（`com.aeroflyconv.app`）与 Public
  （`com.aeroflyconv.converter`）可共存。
