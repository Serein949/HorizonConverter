"""Post-process the generated DOCX: drop the leftover Courier font references."""
import os
import shutil
import zipfile

ROOT = r"C:\Users\17353\Documents\deepseek-harness\default-workspace\AeroflyConv"
DOCX = os.path.join(ROOT, "docs", "Aerofly转换器-手机端使用教程.docx")
TMP = DOCX + ".tmp"

with zipfile.ZipFile(DOCX) as zin, zipfile.ZipFile(TMP, "w", zipfile.ZIP_DEFLATED) as zout:
    for item in zin.infolist():
        data = zin.read(item.filename)
        if item.filename == "word/stylesWithEffects.xml":
            text = data.decode("utf-8")
            text = text.replace('w:ascii="Courier" w:hAnsi="Courier"',
                                'w:ascii="Consolas" w:hAnsi="Consolas"')
            data = text.encode("utf-8")
        elif item.filename == "word/fontTable.xml":
            text = data.decode("utf-8")
            start = text.find('<w:font w:name="Courier">')
            if start != -1:
                end = text.find("</w:font>", start) + len("</w:font>")
                text = text[:start] + text[end:]
            data = text.encode("utf-8")
        zout.writestr(item, data)

shutil.move(TMP, DOCX)
print("patched", DOCX, os.path.getsize(DOCX))
