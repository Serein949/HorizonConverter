"""Build the Chinese mobile user guide (DOCX) for Aerofly Converter 1.22 — Public build only."""
import os

from docx import Document
from docx.enum.table import WD_TABLE_ALIGNMENT
from docx.enum.text import WD_ALIGN_PARAGRAPH
from docx.oxml import OxmlElement
from docx.oxml.ns import qn
from docx.shared import Cm, Pt, RGBColor

ROOT = r"C:\Users\17353\Documents\deepseek-harness\default-workspace\AeroflyConv"
OUT = os.path.join(ROOT, "docs", "Aerofly转换器-手机端使用教程.docx")

ACCENT = RGBColor(0x00, 0x8C, 0xD8)
GREY = RGBColor(0x5A, 0x66, 0x74)
INK = RGBColor(0x1A, 0x22, 0x2C)

doc = Document()

# ---------- base styles ----------
FONT = "Microsoft YaHei"
MONO = "Consolas"


def _force_font(style_obj, name, size=None, bold=None, color=None):
    rPr = style_obj.element.get_or_add_rPr()
    rFonts = rPr.find(qn("w:rFonts"))
    if rFonts is None:
        rFonts = OxmlElement("w:rFonts")
        rPr.insert(0, rFonts)
    for attr in ("w:ascii", "w:hAnsi", "w:eastAsia", "w:cs"):
        rFonts.set(qn(attr), name)
    rFonts.attrib.pop(qn("w:asciiTheme"), None)
    rFonts.attrib.pop(qn("w:hAnsiTheme"), None)
    rFonts.attrib.pop(qn("w:eastAsiaTheme"), None)
    rFonts.attrib.pop(qn("w:cstheme"), None)
    if size:
        style_obj.font.size = Pt(size)
    if bold is not None:
        style_obj.font.bold = bold
    if color is not None:
        style_obj.font.color.rgb = color


# document defaults: one font family everywhere, no theme/latent fonts
_doc_rpr = doc.styles.element.find(qn("w:docDefaults")).find(qn("w:rPrDefault")).find(qn("w:rPr"))
_rd_fonts = _doc_rpr.find(qn("w:rFonts"))
if _rd_fonts is None:
    _rd_fonts = OxmlElement("w:rFonts")
    _doc_rpr.insert(0, _rd_fonts)
for _attr in ("w:ascii", "w:hAnsi", "w:eastAsia", "w:cs"):
    _rd_fonts.set(qn(_attr), FONT)
for _theme_attr in ("w:asciiTheme", "w:hAnsiTheme", "w:eastAsiaTheme", "w:cstheme"):
    _rd_fonts.attrib.pop(qn(_theme_attr), None)

style = doc.styles["Normal"]
_force_font(style, FONT, 10.5, color=INK)
style.paragraph_format.space_after = Pt(6)
style.paragraph_format.line_spacing = 1.35

for name in ("Heading 1", "Heading 2", "Heading 3", "Title", "List Bullet", "List Number",
             "List Bullet 2", "Table Grid"):
    try:
        _force_font(doc.styles[name], FONT)
    except KeyError:
        pass

# kill the leftover "Courier" reference inherited from the default template
for _st in doc.styles:
    try:
        _rPr = _st.element.find(qn("w:rPr"))
    except AttributeError:
        continue
    if _rPr is None:
        continue
    for _rf in _rPr.findall(qn("w:rFonts")):
        for _attr in ("w:ascii", "w:hAnsi", "w:eastAsia", "w:cs"):
            if _rf.get(qn(_attr)) in ("Courier", "Courier New"):
                _rf.set(qn(_attr), MONO)

section = doc.sections[0]
section.left_margin = Cm(2.4)
section.right_margin = Cm(2.4)
section.top_margin = Cm(2.2)
section.bottom_margin = Cm(2.2)

# footer: page number
_footer_para = section.footer.paragraphs[0]
_footer_para.alignment = WD_ALIGN_PARAGRAPH.CENTER
_footer_run = _footer_para.add_run()
_fc_begin = OxmlElement("w:fldChar")
_fc_begin.set(qn("w:fldCharType"), "begin")
_instr = OxmlElement("w:instrText")
_instr.set(qn("xml:space"), "preserve")
_instr.text = "PAGE"
_fc_end = OxmlElement("w:fldChar")
_fc_end.set(qn("w:fldCharType"), "end")
_footer_run._r.append(_fc_begin)
_footer_run._r.append(_instr)
_footer_run._r.append(_fc_end)
_footer_run.font.size = Pt(9)
_footer_run.font.color.rgb = GREY
_footer_run.font.name = FONT
_footer_run._element.rPr.rFonts.set(qn("w:eastAsia"), FONT)


def para(text="", bold=False, size=None, color=None, align=None, space_after=None, italic=False):
    p = doc.add_paragraph()
    run = p.add_run(text)
    run.bold = bold
    run.italic = italic
    run.font.name = FONT
    run._element.rPr.rFonts.set(qn("w:eastAsia"), FONT)
    if size:
        run.font.size = Pt(size)
    if color:
        run.font.color.rgb = color
    if align:
        p.alignment = align
    if space_after is not None:
        p.paragraph_format.space_after = Pt(space_after)
    return p


def heading(text, level=1, page_break=False):
    """Headings are ordinary paragraphs with explicit formatting (no theme fonts)."""
    size, color, before, after = {
        1: (18, ACCENT, 18, 8),
        2: (13.5, INK, 14, 6),
        3: (11.5, INK, 10, 4),
    }[level]
    p = doc.add_paragraph()
    run = p.add_run(text)
    run.bold = True
    run.font.name = FONT
    run._element.rPr.rFonts.set(qn("w:eastAsia"), FONT)
    run.font.size = Pt(size)
    run.font.color.rgb = color
    p.paragraph_format.space_before = Pt(before)
    p.paragraph_format.space_after = Pt(after)
    p.paragraph_format.page_break_before = page_break
    return p


def bullet(text):
    p = doc.add_paragraph(style="List Bullet")
    run = p.add_run(text)
    run.font.name = FONT
    run._element.rPr.rFonts.set(qn("w:eastAsia"), FONT)
    run.font.size = Pt(10.5)
    p.paragraph_format.space_after = Pt(3)
    return p


_list_no = {"n": 0}


def start_list():
    """Restart manual numbering (List Number style keeps counting across the document)."""
    _list_no["n"] = 0


def numbered(text):
    _list_no["n"] += 1
    p = doc.add_paragraph()
    p.paragraph_format.left_indent = Cm(0.8)
    p.paragraph_format.first_line_indent = Cm(-0.8)
    p.paragraph_format.space_after = Pt(3)
    num = p.add_run(f"{_list_no['n']}. ")
    num.font.name = FONT
    num._element.rPr.rFonts.set(qn("w:eastAsia"), FONT)
    num.font.size = Pt(10.5)
    run = p.add_run(text)
    run.font.name = FONT
    run._element.rPr.rFonts.set(qn("w:eastAsia"), FONT)
    run.font.size = Pt(10.5)
    return p


def code(text):
    p = doc.add_paragraph()
    p.paragraph_format.left_indent = Cm(0.6)
    p.paragraph_format.space_before = Pt(2)
    p.paragraph_format.space_after = Pt(8)
    run = p.add_run(text)
    run.font.name = MONO
    run._element.rPr.rFonts.set(qn("w:eastAsia"), MONO)
    run.font.size = Pt(9)
    run.font.color.rgb = RGBColor(0x1F, 0x3B, 0x57)
    return p


def table(headers, rows, widths=None):
    t = doc.add_table(rows=1, cols=len(headers))
    t.style = "Table Grid"
    t.alignment = WD_TABLE_ALIGNMENT.CENTER
    t.autofit = False
    tblPr = t._tbl.tblPr
    layout = tblPr.makeelement(qn("w:tblLayout"), {qn("w:type"): "fixed"})
    tblPr.append(layout)
    trPr = t.rows[0]._tr.get_or_add_trPr()
    trPr.append(OxmlElement("w:tblHeader"))
    hdr = t.rows[0].cells
    for i, h in enumerate(headers):
        hdr[i].text = ""
        p = hdr[i].paragraphs[0]
        run = p.add_run(h)
        run.bold = True
        run.font.size = Pt(9.5)
        run.font.name = FONT
        run._element.rPr.rFonts.set(qn("w:eastAsia"), FONT)
    for row in rows:
        cells = t.add_row().cells
        for i, value in enumerate(row):
            cells[i].text = ""
            p = cells[i].paragraphs[0]
            for j, line in enumerate(str(value).split("\n")):
                if j:
                    p = cells[i].add_paragraph()
                run = p.add_run(line)
                run.font.size = Pt(9.5)
                run.font.name = FONT
                run._element.rPr.rFonts.set(qn("w:eastAsia"), FONT)
    if widths:
        for i, w in enumerate(widths):
            t.columns[i].width = Cm(w)
        for row in t.rows:
            for i, w in enumerate(widths):
                row.cells[i].width = Cm(w)
    doc.add_paragraph().paragraph_format.space_after = Pt(2)
    return t


def figure(path, caption, width=6.0):
    if not os.path.exists(path):
        return
    p = doc.add_paragraph()
    p.alignment = WD_ALIGN_PARAGRAPH.CENTER
    p.paragraph_format.space_before = Pt(8)
    p.paragraph_format.space_after = Pt(2)
    p.add_run().add_picture(path, width=Cm(width))
    cap = doc.add_paragraph()
    cap.alignment = WD_ALIGN_PARAGRAPH.CENTER
    run = cap.add_run(caption)
    run.font.size = Pt(9)
    run.font.color.rgb = GREY
    run.font.name = FONT
    run._element.rPr.rFonts.set(qn("w:eastAsia"), FONT)


def note(text):
    p = doc.add_paragraph()
    p.paragraph_format.left_indent = Cm(0.4)
    p.paragraph_format.space_before = Pt(4)
    p.paragraph_format.space_after = Pt(8)
    run = p.add_run("提示  ")
    run.bold = True
    run.font.size = Pt(9.5)
    run.font.color.rgb = ACCENT
    run.font.name = FONT
    run._element.rPr.rFonts.set(qn("w:eastAsia"), FONT)
    run2 = p.add_run(text)
    run2.font.size = Pt(9.5)
    run2.font.color.rgb = GREY
    run2.font.name = FONT
    run2._element.rPr.rFonts.set(qn("w:eastAsia"), FONT)
    return p


# ---------- cover ----------
for _ in range(4):
    doc.add_paragraph()

para("Aerofly 转换器", bold=True, size=34, color=INK, align=WD_ALIGN_PARAGRAPH.CENTER, space_after=2)
para("手机端使用教程", bold=True, size=20, color=ACCENT, align=WD_ALIGN_PARAGRAPH.CENTER, space_after=18)
para("Aerofly Converter for Android · 版本 1.22", size=11, color=GREY, align=WD_ALIGN_PARAGRAPH.CENTER, space_after=2)
para("全中文界面 · 完全离线 · 无需 root", size=11, color=GREY, align=WD_ALIGN_PARAGRAPH.CENTER)

for _ in range(6):
    doc.add_paragraph()

para("本教程覆盖：安装、界面、三种转换模式、全部选项、换涂装与音效实战、排错与批量转换。",
     size=10, color=GREY, align=WD_ALIGN_PARAGRAPH.CENTER)
para("2026-11", size=9, color=GREY, align=WD_ALIGN_PARAGRAPH.CENTER)

# ---------- 1 ----------
heading("1. 这个 App 能做什么", 1, page_break=True)
para("Aerofly 转换器是一个纯 C# 写成的离线转换工具，专门处理 Aerofly FS Global（手机版）"
     "与 Aerofly FS 4（桌面版）使用的专有文件格式。手机上不需要联网、不需要电脑、不需要 root，装上就能用。")
para("它提供三个转换方向：", space_after=4)
bullet("图片 → TTX：把 PNG / JPG / BMP / PSD 等图片压成游戏用的贴图，支持 DXT1、DXT5、ETC2、R8、"
       "RGBA 无损与 ASTC（手机版贴图格式）；")
bullet("音频 → TSB：把 WAV / MP3 / FLAC / OGG 打包成游戏的音效包；")
bullet("TTX → TTX：把已有贴图换成另一种格式重新编码（例如电脑版做的 DXT 贴图改成手机版用的 ASTC）。")
para("所有转换都在本地完成，不联网、不上传任何文件。")

heading("1.1 支持的输入输出", 2)
table(
    ["方向", "支持的输入", "输出"],
    [
        ["图片 → TTX", "PNG、JPG/JPEG、BMP、GIF、TGA、WebP、PSD、TIFF、HDR、PBM、QOI", ".ttx"],
        ["音频 → TSB", "WAV、MP3、FLAC、OGG", ".tsb"],
        ["TTX → TTX", ".ttx（DXT1/3/5、BC5、ETC1/2、ASTC、RGBA、R8）", ".ttx"],
    ],
    widths=[3.6, 9.2, 3.0],
)
note("转换不会改动原文件：结果始终写到单独的输出目录，同名冲突时会自动加后缀。")

# ---------- 2 ----------
heading("2. 安装", 1)
heading("2.1 用文件管理器安装（推荐）", 2)
start_list()
numbered("把 AeroflyConverter-Public-1.22.apk 拷到手机（微信/QQ 传输、数据线、网盘都行）。")
numbered("在文件管理器里点开 APK，允许「安装未知应用」，然后点安装。")
numbered("如果系统提示「应用未经安全检测」，选择「仍要安装」——这是自签名证书导致的正常提示。")
heading("2.2 用 adb 安装", 2)
para("手机开启开发者选项与「USB 调试」，连上电脑后执行：", space_after=4)
code("adb install -r AeroflyConverter-Public-1.22.apk")
heading("2.3 首次启动", 2)
para("第一次打开会直接进入主界面，没有广告也没有登录。注意两点：")
bullet("文件选择用的是系统文件选择器（SAF），不需要申请「所有文件访问权限」，但选中的文件夹会被记住授权；")
bullet("输出默认写到「下载/AeroflyConv」，这个目录不需要任何权限。")
note("Android 11 之后，其他 App 无法读取 Android/data/<包名>/ 目录。如果游戏资源放在那里，需要 root、Shizuku，"
     "或者用电脑把文件导出来再处理。")

# ---------- 3 ----------
heading("3. 界面速览", 1)
para("整个界面从上到下只有几块：模式、文件、选项、开始转换、日志。纵向滚动即可，没有隐藏菜单。")
para("各区块的作用：", space_after=4)
bullet("模式：一排可以左右滑动的药丸按钮，选中项是青色描边；下面一行小字解释这个模式是做什么的。")
bullet("文件：右上角显示已选文件数量；空的时候提示可以添加哪些扩展名。三个按钮分别是添加文件、文件夹、清空。")
bullet("输出：显示当前输出目录，点「更改」可以挑一个文件夹（例如 SD 卡上的自建目录）。")
bullet("选项：会随模式自动变化，只显示当前模式用得上的选项。")
bullet("开始转换：蓝色大按钮，转换过程中变成「取消」。")
bullet("日志：默认折叠，点标题栏展开；每一行按结果上色（✓ 成功、✕ 失败、! 警告、· 普通）。")
figure(os.path.join(ROOT, r"dist\_phone\pub122.png"), "图 1  主界面：模式、文件、输出、开始转换、日志")

# ---------- 4 ----------
heading("4. 五步完成一次转换", 1)
start_list()
numbered("选模式：图片 → TTX（换贴图）、音频 → TSB（换音效）或 TTX → TTX（换格式）。")
numbered("加文件：点「添加文件」多选，或者点「文件夹」把整个目录递归加进来。")
numbered("设选项：格式、尺寸、翻转、压缩等，多数情况默认值就能用。")
numbered("选输出：默认「下载/AeroflyConv」，也可以点「更改」换目录。")
numbered("点「开始转换」，等进度条走完，点「打开结果」直接看，或「分享」发给别人。")

heading("4.1 添加文件与文件夹", 2)
para("添加文件时选择器不限类型，图片、音频、TTX 都能选；当前模式用不上的文件会被自动跳过，"
     "日志里写「跳过 xxx」，不会让整批失败。")
para("添加文件夹会递归扫描子目录，加进来之后日志会写「文件夹扫描：找到 N 个可转换文件」。")
para("每个文件行左边有一个状态符号，转换时会依次变成 ▶（正在转换）、✓（成功）、✕（失败）、–（跳过）。"
     "行右侧的 ✕ 可以单独移除这个文件。")

heading("4.2 输出位置", 2)
para("默认输出到「下载/AeroflyConv」，在系统的「文件管理 → 下载」里能找到。点「更改」可以选择任意文件夹，"
     "授权会被记住，下次启动继续用。")
para("如果目标目录不可写（比如选了只读的 SD 卡目录），转换会失败并在日志里说明原因。")
note("批量转换时会保留子目录结构：例如把 aircraft/a350 整个文件夹加进来，输出也会按同名子目录分层。")

heading("4.3 转换、取消与结果", 2)
para("转换在后台线程执行，界面不会卡住；进度条下方会显示「正在转换 3/12」这样的计数。"
     "点「取消」会在当前文件结束后停止。")
para("结束后状态行会写「成功 X 个，失败 Y 个」，日志里有每个文件的详细结果，例如：")
code("完成 f14_livery.png -> f14_livery.ttx  type_rgb_astc_6x6 2048x2048 mips=12 (412 ms)")

# ---------- 5 ----------
heading("5. 三种模式详解", 1)
table(
    ["模式", "输入", "输出", "典型用途"],
    [
        ["图片 → TTX", "png jpg bmp psd …", "ttx", "做涂装、改贴图"],
        ["音频 → TSB", "wav mp3 flac ogg", "tsb", "替换/自制音效包"],
        ["TTX → TTX", "ttx", "ttx", "换格式、改尺寸、重压缩"],
    ],
    widths=[3.4, 5.6, 2.4, 5.8],
)

heading("5.1 图片 → TTX（最常用）", 2)
para("这是做涂装的核心模式。选择一张 2 的幂尺寸图片（1024×1024、2048×2048、4096×4096 等），"
     "选好格式与尺寸，转换出来的 .ttx 就可以替换游戏里的同名文件。")
para("支持的图片：PNG、JPG/JPEG、BMP、GIF、TGA、WebP、PSD、TIFF、HDR、PBM、QOI。"
     "PSD 取的是合成后的画面（不取单个图层），带 alpha 的图片会被自动识别。")

heading("5.2 音频 → TSB", 2)
para("直接选择 WAV、MP3、FLAC、OGG，打包成游戏的音效包。内部统一转成 PCM，"
     "所以采样率与声道数由源文件决定（常见 22050 / 44100 Hz、单声道或立体声）。")
para("替换音效时，把输出文件改成与游戏原文件相同的名字即可。")

heading("5.3 TTX → TTX", 2)
para("把一张已有贴图解码，再按新格式重新编码写回。最常见的用途：电脑版做出的 DXT 贴图想在手机上用，"
     "把它转成 ASTC；或者把体积过大的 ASTC 降到 ETC2 / DXT5。")
para("同名输入不会被覆盖，会自动写成 xxx.converted.ttx。实测 2048×2048 的 ASTC 6×6 转成 DXT5 只用 5 秒，"
     "输出 404 KB（LZHAM 压缩 13.8 倍）。")

# ---------- 6 ----------
heading("6. 选项详解", 1)
heading("6.1 格式", 2)
para("格式决定贴图在显卡里怎么存，直接关系到体积与画质。")
table(
    ["格式", "每像素", "画质", "透明通道", "什么时候用"],
    [
        ["自动", "—", "—", "—", "让程序按图片是否带透明通道挑 DXT1/DXT5"],
        ["DXT1", "4 bit", "好", "只有 1 位（全透或全不透）", "不透明涂装，体积最小"],
        ["DXT5", "8 bit", "好", "8 级渐变", "带渐变透明的贴图（玻璃、阴影）"],
        ["ETC2", "4 / 8 bit", "中上", "RGBA 版支持", "老 GLES3 设备，或需要更小体积"],
        ["R8", "8 bit（单通道）", "无损", "无", "遮罩、金属度、粗糙度等单通道数据"],
        ["RGBA 无损", "32 bit", "完全无损", "完整 8 位", "需要绝对保真，或交给别的工具再处理"],
        ["ASTC 6x6", "3.56 bit", "好", "RGB 版无", "手机版贴图首选，体积约为 DXT1 的 1/2"],
        ["ASTC 5x5", "5.12 bit", "很好", "RGBA 版支持", "需要更高画质的手机贴图"],
    ],
    widths=[2.6, 3.2, 2.0, 3.6, 4.8],
)
note("手机版游戏（Aerofly FS Global）的贴图是 ASTC；桌面版 FS 4 用 DXT。改哪边的贴图，就用哪边的格式。")

heading("6.2 贴图尺寸", 2)
para("「匹配原尺寸」是默认值，也是最安全的选择：如果目标位置已经有一张同名 TTX，程序会读它的尺寸和格式，"
     "按同样的规格输出。这样替换进游戏不会出现拉伸、错位或整块变黑。")
para("其它选项（4096 / 2048 / 1024 / 源尺寸）用于新建贴图，或者故意提高/降低分辨率。"
     "尺寸必须是 2 的幂，非 2 的幂会被自动缩到最近的 2 的幂。")
note("曾经出现过「换成 6136×6132 的图后游戏里涂装全黑」的情况，原因就是尺寸和原槽位不符。用「匹配原尺寸」可以避免。")

heading("6.3 编码质量（仅 ASTC）", 2)
para("只有格式选 ASTC 时才会出现。三档的差别是搜索强度，画质差距很小，速度差 2–5 倍：")
table(
    ["档位", "2048² 桌面耗时", "画质 (PSNR)", "建议"],
    [
        ["快速", "3.4 s", "41.78 dB", "批量转、先看效果"],
        ["标准", "9.2 s", "41.99 dB", "默认，日常用这个"],
        ["高质量", "17.6 s", "42.04 dB", "最终成品"],
    ],
    widths=[3.0, 5.0, 4.4, 6.0],
)

heading("6.4 垂直翻转", 2)
para("Aerofly 的贴图行序与常见的图片查看器相反（自下而上）。开启后（默认）输出的贴图在游戏里方向正确。")
para("判断方法：转换后把 PNG 用系统相册打开，如果图像上下颠倒，就关掉这个开关再转一次。")
note("翻转只影响行序，不影响像素本身。RGBA 无损格式在关闭翻转时与源图逐像素一致。")

heading("6.5 压缩 TTX（LZHAM）", 2)
para("游戏自己的 TTX 都是用 LZHAM 压过的，开启后输出也是同样的容器格式，游戏能直接读。"
     "典型压缩率约 8–9 倍（实测 2,796,528 字节压到 324,992 字节）。")
para("关掉它文件会大很多（未压缩的 2048² DXT1 约 2.7 MB → 压缩后约 325 KB），一般不需要关。")

# ---------- 7 ----------
heading("7. 实战一：替换飞机涂装", 1)
para("以把一架 A350 的自定义涂装换进游戏为例。")
heading("7.1 准备图片", 2)
bullet("尺寸：与原贴图一致（一般 2048×2048 或 4096×4096）。")
bullet("内容：在 Photoshop / Affinity / GIMP 里按 UV 布局放好图案，导出 PNG。")
bullet("透明：有 alpha 的图请选带 alpha 的格式（DXT5 / ETC2 的 RGBA 版 / ASTC 5x5 / RGBA 无损）。")
para("如果想把原版贴图当模板（保留原有细节），可以用桌面版把它转成 PNG 再编辑；手机端只负责把改好的图压回 TTX。")

heading("7.2 转换设置建议", 2)
table(
    ["选项", "建议值", "原因"],
    [
        ["格式", "ASTC 6x6（手机）", "与游戏原文件一致"],
        ["贴图尺寸", "匹配原尺寸", "避免整块变黑/拉伸"],
        ["垂直翻转", "开", "行序与游戏一致"],
        ["压缩 TTX", "开", "体积小、游戏能直读"],
        ["编码质量", "标准", "速度与画质平衡"],
    ],
    widths=[3.4, 7.4, 6.0],
)

heading("7.3 放回游戏", 2)
para("把生成的 .ttx 改成与原文件相同的文件名，覆盖到原来的位置（建议先备份原文件）。"
     "覆盖时保持目录结构不变，重新进入游戏即可看到新涂装。")

# ---------- 8 ----------
heading("8. 实战二：替换音效", 1)
start_list()
numbered("准备新的音频，导出为 WAV（PCM 16 bit），或用现成的 MP3 / FLAC / OGG。")
numbered("用「音频 → TSB」打包成 TSB，文件名与游戏原文件保持一致。")
numbered("覆盖回游戏目录（建议先备份），进游戏试听。")
note("采样率/声道数会跟随源文件。若游戏对格式敏感，最稳妥的是与原文件保持相同的采样率（常见 22050 或 44100 Hz）。")

# ---------- 9 ----------
heading("9. 日志与排错", 1)
figure(os.path.join(ROOT, r"dist\_phone\log1216.png"), "图 2  展开后的日志：行数统计、复制、清空，以及按结果上色的明细", width=5.6)
heading("9.1 日志怎么用", 2)
para("日志标题右边显示当前行数，点「复制」把全部日志复制到剪贴板（排查问题很有用），点「清空」清掉历史。"
     "转换出现失败时日志会自动展开。")

heading("9.2 常见提示", 2)
table(
    ["提示", "含义", "怎么办"],
    [
        ["跳过 xxx", "文件与当前模式不匹配", "换成对应的模式再转"],
        ["不支持的图片格式", "文件损坏或不是真正的图片", "用看图软件确认能打开，重新导出"],
        ["无法打开 / 写入失败", "目标目录没有权限或磁盘满", "换输出目录，或清理空间"],
        ["尺寸不是 2 的幂", "图片尺寸不符合 GPU 要求", "改用「匹配原尺寸」或手动缩到 1024/2048"],
        ["闪退", "程序异常退出", "重开应用；若反复出现，把 Android/data/com.aeroflyconv.converter/files/crash.txt 发我"],
    ],
    widths=[4.6, 6.0, 6.2],
)

heading("9.3 速度参考（vivo V2304A / Android 16）", 2)
table(
    ["任务", "耗时", "输出体积"],
    [
        ["1024² 图片 → TTX（RGBA 无损 + LZHAM，11 mips）", "约 1.3 s", "569 KB"],
        ["512² 图片 → TTX（ASTC 6x6 + LZHAM，10 mips）", "约 0.4 s", "62 KB"],
        ["2048² TTX（ASTC）→ TTX（DXT5）", "5 s", "404 KB"],
        ["3 张 1024² 图片（PNG/JPG/BMP）批量", "4.7 s", "—"],
    ],
    widths=[9.6, 3.2, 3.6],
)

# ---------- 10 ----------
heading("10. 高级：adb 批量转换", 1)
para("如果一次要处理上百个文件，可以不开界面直接跑。把文件推进应用私有目录，然后用一条命令触发：")
code("adb push *.png /sdcard/Android/data/com.aeroflyconv.converter/files/in/\n"
     "adb shell am start -n com.aeroflyconv.converter/crc64c6fe54825405fced.MainActivity \\\n"
     "  --ez autorun true --es mode png2ttx --es format astc6x6 --ez flip true")
para("参数一览：", space_after=4)
table(
    ["参数", "取值", "说明"],
    [
        ["--ez autorun", "true", "启动后立刻转换 in 目录里的文件"],
        ["--es mode", "png2ttx / audio2tsb / ttx2ttx", "转换模式"],
        ["--es format", "dxt1 / dxt5 / etc2 / r8 / type_rgba / astc6x6 / astc5x5", "输出格式"],
        ["--es quality", "fast / standard / high", "ASTC 质量档"],
        ["--ez flip", "true / false", "垂直翻转"],
    ],
    widths=[3.4, 8.0, 5.4],
)
para("结果写在 files/out/，同时生成 result.txt 汇总。")

# ---------- 11 ----------
heading("11. 常见问题", 1)
qa = [
    ("转了之后游戏里贴图全黑 / 花屏？",
     "先确认尺寸与原槽位一致（用「匹配原尺寸」），格式与游戏原文件一致——手机版请务必用 ASTC。"
     "如果用的是 1.21.6 之前的版本，升级到 1.22 即可（旧版 ASTC 编码有缺陷会产生黑块）。"),
    ("转出来的图是上下颠倒的？",
     "把「垂直翻转」关掉再转一次。这只影响行序，不影响画质。"),
    ("为什么文件比原版大很多？",
     "确认「压缩 TTX（LZHAM）」是开着的；另外 RGBA 无损本身就是未压缩数据，体积会比 DXT 大 6–8 倍。"),
    ("能读 PSD 吗？",
     "能，取的是合成后的画面。分层 PSD 请先在 Photoshop 里合并图层或导出 PNG。"),
    ("支持哪些音频？",
     "WAV、MP3、FLAC、OGG。打包成 TSB 时内部统一转成 PCM。"),
    ("转换会不会改动原文件？",
     "不会。TTX → TTX 遇到同名会写成 xxx.converted.ttx，其它模式都输出到单独的目录。"),
    ("需要联网吗？",
     "完全离线，没有任何网络请求。"),
    ("手机上没有我要的贴图文件？",
     "Android 11+ 限制访问 Android/data/，需要 root/Shizuku，或先用电脑处理再传回手机。"),
    ("日志里的毫秒数代表什么？",
     "单个文件的转换耗时，可以拿来对比不同格式/质量档的速度。"),
]
for q, a in qa:
    para(q, bold=True, size=10.5, space_after=2)
    para(a, size=10, color=GREY, space_after=8)

# ---------- 12 ----------
heading("12. 版本与校验", 1)
para("当前版本 1.22。APK 的 SHA256（前 16 位）：")
table(
    ["文件", "SHA256（截断）"],
    [
        ["AeroflyConverter-Public-1.22.apk", "e5c23615d0be1cb3…"],
    ],
    widths=[8.0, 8.8],
)
para("完整校验值见发布包里的 SHA256SUMS.txt。升级时直接覆盖安装即可，设置会被保留。")
para("桌面版（CLI / GUI）与手机版共用同一套引擎，当前同样是 1.22。")

doc.save(OUT)
print("saved", OUT, os.path.getsize(OUT))
