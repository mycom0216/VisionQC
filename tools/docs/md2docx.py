"""간단한 마크다운 → Word(.docx) 변환기. 제출 문서(기획서·결과보고서) 생성용.
사용법: python tools/md2docx.py docs/StudyMate_기획서.md docs/StudyMate_기획서.docx
지원: # ## ### 제목, 표, 코드블록, 이미지, 글머리표/번호 목록, **굵게**, `코드`
"""
import re
import sys
from pathlib import Path

from docx import Document
from docx.enum.text import WD_ALIGN_PARAGRAPH
from docx.oxml import OxmlElement
from docx.oxml.ns import qn
from docx.shared import Cm, Pt, RGBColor

FONT = "Malgun Gothic"


def set_font(run, size=None, bold=None, mono=False, color=None):
    name = "Consolas" if mono else FONT
    run.font.name = name
    run._element.rPr.rFonts.set(qn("w:eastAsia"), FONT)
    if size:
        run.font.size = Pt(size)
    if bold is not None:
        run.font.bold = bold
    if color:
        run.font.color.rgb = RGBColor.from_string(color)


def add_inline(par, text, size=10.5):
    for part in re.split(r"(\*\*[^*]+\*\*|`[^`]+`)", text):
        if not part:
            continue
        if part.startswith("**"):
            set_font(par.add_run(part[2:-2]), size, True)
        elif part.startswith("`"):
            set_font(par.add_run(part[1:-1]), size - 0.5, mono=True, color="4F46E5")
        else:
            set_font(par.add_run(part), size)


def shade(cell, hex_fill):
    tcPr = cell._element.get_or_add_tcPr()
    shd = OxmlElement("w:shd")
    shd.set(qn("w:val"), "clear")
    shd.set(qn("w:color"), "auto")
    shd.set(qn("w:fill"), hex_fill)
    tcPr.append(shd)


def convert(src, dst):
    lines = Path(src).read_text(encoding="utf-8").splitlines()
    base = Path(src).parent
    doc = Document()
    sec = doc.sections[0]
    sec.left_margin = sec.right_margin = Cm(2.2)
    sec.top_margin = sec.bottom_margin = Cm(2.0)
    doc.styles["Normal"].font.name = FONT
    doc.styles["Normal"].element.rPr.rFonts.set(qn("w:eastAsia"), FONT)
    i = 0
    while i < len(lines):
        ln = lines[i]
        if ln.startswith("```"):
            i += 1
            block = []
            while i < len(lines) and not lines[i].startswith("```"):
                block.append(lines[i])
                i += 1
            p = doc.add_paragraph()
            p.paragraph_format.left_indent = Cm(0.4)
            for k, b in enumerate(block):
                set_font(p.add_run(b + ("\n" if k < len(block) - 1 else "")), 8.5, mono=True)
            i += 1
            continue
        if ln.startswith("|") and i + 1 < len(lines) and re.match(r"^\|[\s:|-]+\|$", lines[i + 1]):
            rows = []
            while i < len(lines) and lines[i].startswith("|"):
                if not re.match(r"^\|[\s:|-]+\|$", lines[i]):
                    rows.append([c.strip() for c in lines[i].strip().strip("|").split("|")])
                i += 1
            t = doc.add_table(rows=len(rows), cols=len(rows[0]))
            t.style = "Table Grid"
            # 열 너비를 내용 길이에 비례해 정하고 합계를 본문 폭(약 17cm) 안에 맞춥니다.
            # 모든 열을 같은 폭으로 두면 긴 문장 열은 좁아져 읽기 어렵고, 표가 여백 밖으로 약간 삐져나옵니다.
            t.autofit = False
            weights = [min(max(len(re.sub(r"[*`]", "", row[c])) for row in rows), 40) + 6 for c in range(len(rows[0]))]
            total = sum(weights)
            for c, wt in enumerate(weights):
                for r in range(len(rows)):
                    t.cell(r, c).width = Cm(16.6 * wt / total)
            for r, row in enumerate(rows):
                for c, txt in enumerate(row):
                    cell = t.cell(r, c)
                    cell.text = ""
                    par = cell.paragraphs[0]
                    pm = re.match(r"!\[[^\]]*\]\(([^)]+)\)", txt)
                    if pm and (base / pm.group(1)).exists():
                        par.add_run().add_picture(str(base / pm.group(1)), width=Cm(7.4))
                        continue
                    add_inline(par, txt, 9.5)
                    if r == 0:
                        shade(cell, "ECEAFD")
                        for run in par.runs:
                            run.font.bold = True
            doc.add_paragraph()
            continue
        m = re.match(r"!\[[^\]]*\]\(([^)]+)\)", ln)
        if m:
            f = base / m.group(1)
            if f.exists():
                # 세로로 긴 그림이 쪽 높이를 넘어 잘리지 않도록 폭을 줄입니다(최대 폭 15cm, 최대 높이 21cm).
                from PIL import Image as _Img
                _w, _h = _Img.open(f).size
                doc.add_picture(str(f), width=Cm(min(15, 21 * _w / _h)))
                doc.paragraphs[-1].alignment = WD_ALIGN_PARAGRAPH.CENTER
            i += 1
            continue
        if ln.startswith("# "):
            p = doc.add_paragraph()
            set_font(p.add_run(ln[2:]), 22, True, color="3730A3")
        elif ln.startswith("## "):
            p = doc.add_paragraph()
            p.paragraph_format.space_before = Pt(14)
            set_font(p.add_run(ln[3:]), 15, True, color="3730A3")
        elif ln.startswith("### "):
            p = doc.add_paragraph()
            p.paragraph_format.space_before = Pt(8)
            set_font(p.add_run(ln[4:]), 12, True)
        elif re.match(r"^\s*- ", ln):
            p = doc.add_paragraph(style="List Bullet")
            add_inline(p, re.sub(r"^\s*- ", "", ln))
        elif re.match(r"^\d+\. ", ln):
            p = doc.add_paragraph(style="List Number")
            add_inline(p, re.sub(r"^\d+\. ", "", ln))
        elif ln.strip():
            p = doc.add_paragraph()
            add_inline(p, ln)
        i += 1
    doc.save(dst)
    print("saved", dst)


if __name__ == "__main__":
    convert(sys.argv[1], sys.argv[2])
