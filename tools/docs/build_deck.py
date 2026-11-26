"""발표자료 보강: python tools/docs/build_deck.py
기존 포트폴리오 발표자료(outputs/visionqc-20261117/VisionQC_포트폴리오.pptx, 18장)에 빠져 있던 그림을 넣습니다.
  · 검사 처리 순서도(아키텍처 슬라이드 다음)
  · 재판정·권한 순서도
  · ERD(데이터 모델 슬라이드 다음): 실제 구현 스키마 6개 테이블
기존 슬라이드는 그대로 두고, 새 슬라이드는 기존 슬라이드의 제목·쪽번호·본문 도형을 복사해 같은 모양으로 만듭니다.
쪽번호는 도형 안의 고정 글자라서 순서를 바꾼 뒤 다시 매깁니다.
"""
import copy
import re
from pathlib import Path

from pptx import Presentation
from pptx.util import Emu, Inches

ROOT = Path(__file__).resolve().parent.parent.parent          # VisionQC/
SRC = ROOT.parent / "outputs" / "visionqc-20261117" / "VisionQC_포트폴리오.pptx"
IMG = ROOT / "docs" / "submission" / "images"
OUT = ROOT / "docs" / "submission" / "VisionQC_발표자료.pptx"

prs = Presentation(str(SRC))
slides = list(prs.slides)
ref = slides[4]  # '시스템 아키텍처' 슬라이드: 제목·쪽번호·본문 모양의 견본

# 견본 도형 찾기: 제목(맨 위 큰 글자), 쪽번호("05"), 아래 설명줄(마지막 글상자)
shapes = [s for s in ref.shapes if s.has_text_frame and s.text_frame.text.strip()]
title_src = next(s for s in shapes if s.text_frame.text == "시스템 아키텍처")
num_src = next(s for s in shapes if re.fullmatch(r"\d\d", s.text_frame.text))
note_src = shapes[-1]


def clone(slide, src, text, left=None, top=None, width=None, height=None):
    """견본 도형을 새 슬라이드에 복사하고 글자만 바꿉니다(글꼴·색·크기는 견본 그대로)."""
    el = copy.deepcopy(src._element)
    slide.shapes._spTree.append(el)
    shp = slide.shapes[-1]
    paras = shp.text_frame.paragraphs
    paras[0].runs[0].text = text
    for r in paras[0].runs[1:]:
        r.text = ""
    for extra in paras[1:]:                      # 견본의 여분 문단 제거
        extra._p.getparent().remove(extra._p)
    if left is not None:
        shp.left, shp.top, shp.width, shp.height = left, top, width, height
    return shp


def new_slide(title, image, bullets):
    s = prs.slides.add_slide(prs.slide_layouts[5])  # 'Blank'
    clone(s, title_src, title)
    clone(s, num_src, "00")
    pic_h = Inches(5.5)
    pic = s.shapes.add_picture(str(IMG / image), Inches(0.6), Inches(1.25), height=pic_h)
    if pic.width > Inches(7.4):                       # 가로로 넓은 그림은 폭에 맞춤
        ratio = Inches(7.4) / pic.width
        pic.width, pic.height = int(pic.width * ratio), int(pic.height * ratio)
    left = pic.left + pic.width + Inches(0.4)
    for i, text in enumerate(bullets):
        clone(s, note_src, text, left, Inches(1.5) + i * Inches(1.1), prs.slide_width - left - Inches(0.5), Inches(0.95))
    return s


a = new_slide("검사 처리 순서도", "flow-inspection.png", [
    "로그인 → 설정 → 모델 검증 → 회차 시작",
    "이미지마다 전처리 → ONNX 추론 → 판정(점수 ≥ 임계값이면 NG)",
    "예외·무효 점수는 ERROR로 기록하고 다음 이미지 계속",
    "원 판정을 SQLite에 저장, NG+CAM이면 히트맵도 저장",
])
b = new_slide("재판정·권한 순서도", "flow-review.png", [
    "엔지니어 이상만 정정 가능(서버에서도 검사)",
    "원 판정은 수정하지 않고 정정 행을 추가",
    "가장 나중(id가 큰) 정정이 유효, 감사 로그에 기록",
])
c = new_slide("ERD (실제 구현 스키마)", "erd.png", [
    "6개 테이블: 회차 → 결과 → 정정, 불량(현재 비어 있음)",
    "계정·감사 로그는 아이디 문자열로 연결(외래 키 아님)",
])

# 순서 바꾸기: 원본 0~4, A, B, 원본 5~8, C, 원본 9~끝
lst = prs.slides._sldIdLst
ids = list(lst)
orig, (ia, ib, ic) = ids[: len(slides)], ids[len(slides):]
order = orig[:5] + [ia, ib] + orig[5:9] + [ic] + orig[9:]
for el in ids:
    lst.remove(el)
for el in order:
    lst.append(el)

# 쪽번호 다시 매기기(두 자리 숫자만 있는 글상자)
for n, slide in enumerate(prs.slides, start=1):
    for shp in slide.shapes:
        if shp.has_text_frame and re.fullmatch(r"\d\d", shp.text_frame.text.strip() or "x"):
            shp.text_frame.paragraphs[0].runs[0].text = f"{n:02d}"

prs.save(str(OUT))
print("saved", OUT, "slides:", len(prs.slides))
