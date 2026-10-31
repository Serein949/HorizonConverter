# AeroflyConv — 快得多的 Aerofly FS 转换器（C# / .NET 10）

[`RishiK-06/AeroflyFS-Native-Converter`](https://github.com/RishiK-06/AeroflyFS-Native-Converter) 的 C# 重写版：
功能一一对照，但整条处理管线都是原生代码 —— 纯托管 C# 的 LZHAM 编解码器、SIMD 的
BC1/BC3/ETC2 纹理编解码、自写的高吞吐 PNG 编码器、多文件并行批处理。

![GUI](docs/screenshot.png)

## 为什么快

| 环节 | 原版（Python + PySide6） | 本版（C# / .NET 10） |
|---|---|---|
| LZHAM | `wasmtime` 跑 `tmcompress.wasm`，压缩靠 `pylzham` | 纯 C# 移植的解码器 + raw-block 编码器（无原生依赖） |
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
| PNG → TTX（1024² DXT1，含 mip 链 + LZHAM） | 11.1 s | 0.37 s（30×） | — |

LZHAM 本体：解码 **162 MB/s**、编码 **728 MB/s**（与官方 C++ 版本产出的字节流逐字节一致）。

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
  编码解码器（sync/raw/comp/EOF 四种 block、全部符号模型、Huffman 前缀表）。编码方向只写
  raw block，但块头、对齐与结尾 adler32 完全按规范编码。
* **纹理编解码**（`Codecs/`）：BC1/BC2/BC3/BC4/BC5 与 ETC1/ETC2/EAC 解码、BC1/BC3 与
  ETC2 RGB/RGBA 编码，全部 SIMD 手写；解码与 `texture2ddecoder` 逐字节一致。
* **容器**（`Tm/`、`Textures/`）：TTX/TTC/TSB/TM 的 chunk 树、mip 链、`compress_file`
  包装、CRC32、tmcompress 头，按原版语义重写（并兼容 IPACS 的 `string8u` 变体）。
* **PNG**：读用 ImageSharp，写用自写编码器（RGB/RGBA、逐行 MSAD 自适应滤波）。
* **音频**：NLayer（MP3）、NVorbis（OGG）、FlacBox（FLAC），全部纯托管。
* **并行**：文件级并行；单文件内部按顺序处理。

---

## 5. 验证

`tests/vectors` 里是从**真实游戏文件**生成并与原版 Python 工具对照过的参考数据：

* 24 个真实容器（.ttx / .toc / .tmb / .tsc / .tsl / .wad / .tsb）的 LZHAM 解压结果哈希
* 纹理解码后的 RGBA 哈希（含 DXT1/DXT5/BC5/ETC2）
* TM→文本树的完整文本哈希（含 55 MB 的巨大 .tmb）
* TSB→WAV 字节哈希
* 原版实现（真 `pylzham` 压缩流）编码出的 TTX，用于验证 C# 解码器能吃"真压缩块"
* C# 编码出的 TTX 已用原版解码器 + 官方 LZHAM wasm 反向验证可读

```powershell
dotnet run --project tools/Verify       # 63 项端到端对照（当前全部通过）
dotnet run --project tools/LzhamCheck   # LZHAM 24 个解码向量 + 编码回环 + 吞吐
dotnet run --project tools/CodecCheck   # 块压缩编解码 vs texture2ddecoder / etcpak
```

---

## 6. 已知限制（与原版的差异）

* **ASTC**（`type_rgb_astc_*` / `type_rgba_astc_*`，仅 Aerofly 移动版用）不解码，会给出明确报错。
* 游戏自带的 `scenery/images/map_*.ttc` 切片（头部格式标记 `0x1234567x`）是未公开的另一种
  变体，原版 Python 工具同样无法读取；本版给出明确提示。转换器自己生成的 TTC 正常工作。
* **BC5 法线贴图**：本版按法线贴图规范重建 Z 通道（正确）；原版总是用零通道推导 Z，
  所以两者蓝色通道不同（红/绿通道逐字节一致）。这是有意修正。
* **TTX 压缩写法**：用 LZHAM *raw block*（合法、游戏可读、已用官方解码器验证），但没有 LZ
  匹配，文件体积接近未压缩大小。需要原版那种小体积时用 `--no-compress` 写非压缩容器，
  或仍用原版 Python 的 `pylzham` 压缩。
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

## 8. �������⣺����һ��������ˣ�NahimicOSD.dll��

Windows �¼���־�����������

```
Faulting application name: AeroflyConv.exe
Faulting module name: NahimicOSD.dll, version: 2.2.25.0
Exception code: 0xc0000005
```

�ⲻ�Ǳ���������⣺Dell / Alienware �����ϵ� **Alienware Sound Center��Nahimic��** ���
`NahimicOSD.dll` ע�뵽ÿ�����̣������ģ������֪�ı���ȱ�ݣ�ԭ�� 0xC0000005����

�����취����ѡ��һ����

1. ˫������Ŀ¼��� `NahimicOSD-ֹͣ.cmd`�����������ԱȨ�ޣ��������� AeroflyConv��
   ��Ҫ��ԭʱ���� `NahimicOSD-�ָ�.cmd`��
2. `services.msc` �� �ҵ� `NahimicService` �� ֹͣ�������������͸ĳɡ����á���������Ч����
3. �� Alienware Sound Center / Nahimic ��ر� OSD / ����׷����ĵ��㹦�ܡ�
4. ֻ�������а棨`dist\cli\AeroflyConv.exe`�������������������ڣ��������ױ� OSD ������

��������ʱ�����⵽�� DLL ��ע�룬������־�������ɫ���沢������ʾ��

---

## 9. �������⣺`Access to the path ... is denied`

ת�����Ĭ��д��Դ�ļ��Աߡ���Ϸ��װĿ¼��`...\SteamLibrary\steamapps\common\Aerofly FS 4 ...`��
һ�㲻������ͨ�û�д�룬���ԻᱨȨ�޴������ֽ���취��

1. **GUI**��`Options �� Output` ѡһ����дĿ¼����дĿ¼����ļ��нṹ�ᰴ���뾵�񣩣�
   �Ҳ� `?` �ָ�"д��Դ�ļ��Ա�"��
2. **CLI**��`-d/--outdir`

   ```powershell
   AeroflyConv "E:\SteamLibrary\steamapps\common\Aerofly FS 4 Flight Simulator\aircraft\a350_1000" -d D:\AeroflyPNG -j 16
   ```

3. **ʲô������**��������Զ���д�� `Documents\AeroflyConv-out`����Ҳ����д�����γ���
   `%LOCALAPPDATA%\AeroflyConv\out`����ʱĿ¼��exe ͬĿ¼ `out\`������־���ע��ʵ��λ�á�

���� "auto" ģʽ��ɨ���ļ���ʱֻ������֪��������չ����`.ttx .tsb .wav .mp3 .flac .ogg .ttc
.toc .tsc .wad .tmb .tsl .zip .tme` ��ͼƬ���������ٶ� `.json`��`.cfg` ֮����ļ���"�޷�ʶ��"��

---

## 10. ��ͼ������"��ɫ����"

* **���µߵ�**��Aerofly �� TTX ��ͼ�����¶��ϵ�˳��洢������ PNG ʱĬ��**�Ѿ���ת**��
  ���Դ򿪾��������ģ�GUI �� `Flip vertically (Aerofly stores textures bottom-up)` Ĭ�Ϲ�ѡ��
  �����п��� `--no-flip` �رգ���д�� TTX ʱҲĬ�Ϸ�ת�����"�������༭������"�ܶ��ϡ�
* **ĳ��������ʾΪ����**������ **alpha = 0 ��͸������**����Ƭ�鿴�����͸�����ɺ�ɫ
  ��A350 �� 276 �� RGBA ������� 183 �ŵײ�����ȫ͸����������ͼ���������ݣ���
  �� Photoshop / GIMP / ֧��͸��ͨ���Ĳ鿴���򿪾���͸���ġ�ת����־�����ڻ���ʾ
  "x% of the pixels are transparent (alpha=0)"��

## 11. �û�Ŀ¼���Ϳװ TTX��ԭ������˵����֣�

`Documents\Aerofly FS 4\aircraft\...` �������Ϳװ `.ttx` �õ�����һ�����ͷ��
ǰ `0x40` �ֽ��� `0x5A` ������ magic Ϊ `a5 a4 24 c7 b7 3c eb b7`��
ԭ�� Python ���߻�ֱ�ӱ� "not a TTX texture container"��������ʶ�𲢽��
�����������ͨ�� LZHAM `compress_file` ��������ʵ������� `Documents\Aerofly FS 4\aircraft`
Ŀ¼�����Ϻ� A330 ��Ϳװ��25 ���ļ� 3.8 ��ת����ɡ�

---

## 12. ��ͼ�ߴ� / ����ԭ��ͼ����Ҫ��

��Ϸ���ÿ����ͼ"��λ"���й̶��ߴ�͸�ʽ��A350 ���� `ext01_fuselage_color.ttx` ��
**2048��2048 / DXT1 / 12 �� mip**�������д��ȥ�� TTX �ߴ粻�ԣ����� 6136��6132��
�� 2 ���ݡ�13 �� mip������Ϸ�����ͻ��λ/����ʧ�ܡ�������Ϊ������������ڡ����ֲ�����
������"���µߵ�"��

v1.1 �Զ�������

* **�����Ѵ��ڵ� .ttx ʱ**����ȡ����ͼԭ�еĿ��ߺ� GPU ��ʽ����Դͼ����/ת��һ�µ�
  ��`--no-fit` �ɹرգ���
* **Ŀ�겻����ʱ**���� 2 ���ݵ�ͼƬ�Զ���������� 2 ���ݣ��� 6136��6132 �� 4096��4096����
* ���������ƽ������С��/ ˫���ԣ��Ŵ󣩣�GIMP/PS ֮�ⲻ��Ҫ�Լ��ȸĳߴ硣

ʵ�⣺��ԭ��ͼ������ PNG ��ԭ��д�أ��ߴ�/��ʽ/mip ����ԭ�ļ�һ�£����� PSNR 46.8 dB��
���ļ������� IPACS ԭ�����Ϊ LZHAM д���� raw block����Ӱ���ȡ����

### ����ߴ���ôѡ

����������ͼʱĬ�� **Match original**����ԭ��λһ�£��ȫ������Ҫ����������ѡ����ߴ磺

| ѡ�� | �����Դͼ 6136��6132�� | ˵�� |
|---|---|---|
| Match original | 2048��2048 / 12 mips��2.7 MB | ��ԭ���λ��ȫһ�� |
| 4096 px | 4096��4096 / 13 mips��10.7 MB | �� 2048 ��������ϸ�ڣ��Ƽ� |
| 2048 px | 2048��2048 | ǿ�ƽ����� |
| 8192 px | 8192��8192 / 14 mips��42.7 MB | �ѳ���Դͼ�ֱ��ʣ�ֻ�ǷŴ󣬲�����ϸ�� |
| Source (max PO2) | 4096��4096 | ȡ������Դͼ�� 2 ���� |

�����У�`--size 4096`���Դ�/����ʱ�����������������40962 ԼΪ 20482 �� 4 ����
81922 ���� 40962 �� 4 ����DXT1 ����Լ 11 MB �� 43 MB���� mip����
