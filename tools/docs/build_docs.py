"""제출용 문서 묶음 생성: python tools/docs/build_docs.py
docs/*.md(원본 문서) → docs/submission/ 에 "그림이 들어간" 제출용 md 를 만들고, md2docx.py 로 .docx 를 만든 뒤
(PDF 는 docx2pdf.ps1 로 변환) 한 폴더에 모읍니다.

왜 원본 md 를 직접 변환하지 않나?
  원본 설계서·데이터정의서는 순서도·ERD 를 mermaid 코드 블록으로 적어 두었습니다. GitHub 에서는 그림으로 보이지만
  Word/PDF 변환기는 mermaid 를 그리지 못해 코드가 그대로 찍힙니다. 그래서 변환 전에 mermaid 블록을
  미리 그려 둔 PNG(실제 구현 ERD, 순서도)로 바꿔 넣은 사본을 만듭니다. 원본 md 는 건드리지 않습니다.
"""
import re
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent      # VisionQC/
DOCS = ROOT / "docs"
OUT = DOCS / "submission"
IMG = OUT / "images"
OUT.mkdir(parents=True, exist_ok=True)
IMG.mkdir(parents=True, exist_ok=True)

# 실제 구현 스키마에서 그린 ERD(outputs 폴더의 build_diagrams.mjs 가 생성)를 문서용으로 복사
ERD_SRC = ROOT.parent / "outputs" / "visionqc-20261117" / "assets" / "VisionQC_ERD.png"
if ERD_SRC.exists():
    shutil.copyfile(ERD_SRC, IMG / "erd.png")

MERMAID = re.compile(r"```mermaid\n.*?```\n", re.S)


def replace_mermaid(text: str, replacements: list[str]) -> str:
    """문서 안의 mermaid 블록을 나오는 순서대로 replacements 의 마크다운으로 바꿉니다(개수가 다르면 중단)."""
    blocks = MERMAID.findall(text)
    if len(blocks) != len(replacements):
        sys.exit(f"mermaid 블록 {len(blocks)}개인데 교체 문구는 {len(replacements)}개입니다.")
    it = iter(replacements)
    return MERMAID.sub(lambda _m: next(it) + "\n", text)


ERD_MD = "![VisionQC ERD (실제 구현 스키마, 6개 테이블)](images/erd.png)\n\n실선은 SQLite 외래 키, 점선은 논리적 연관(외래 키 아님)입니다."

# (원본 파일, 제출용 파일 이름, mermaid 교체 목록, 앞에 붙일 머리말)
JOBS = [
    ("01_분석서.md", "VisionQC_요구사항분석서.md", [], ""),
    ("03_요구사항명세서.md", "VisionQC_요구사항명세서.md", [], ""),
    ("04_데이터정의서.md", "VisionQC_데이터정의서.md", [ERD_MD], ""),
    ("02_설계서.md", "VisionQC_설계서.md", [
        "![검사 처리 순서도](images/flow-inspection.png)\n\n위 순서도는 폴더 일괄 검사를 기준으로 한 실제 처리 흐름입니다(생산자-소비자 대기열, 이미지별 ERROR 처리, 저장).",
        "검사 회차(InspectionJob) 상태: `Running`(진행) → `Completed`(모든 이미지 처리) / `Cancelled`(사용자 중지) / `Failed`(입력 소스 오류 등 치명적 오류). "
        "종료 상태에서는 다시 진행 상태로 돌아가지 않습니다.",
        "> 설계 단계에서는 USER·MODEL_VERSION 테이블까지 계획했으나, 구현 과정에서 모델 정보를 `inspection_job`에 직접 기록하고 계정은 `user_account`로 분리해 "
        "**아래의 6개 테이블**로 정리했습니다(상세는 데이터정의서).\n\n" + ERD_MD,
    ], ""),
    ("06_포트폴리오.md", "VisionQC_포트폴리오.md", [], ""),
]


def main() -> None:
    for src, dst, repl, head in JOBS:
        text = (DOCS / src).read_text(encoding="utf8")
        text = replace_mermaid(text, repl) if repl else text
        # 링크가 같은 폴더의 다른 md 를 가리키면 제출용 파일 이름으로는 깨지므로 링크 대신 글자만 남깁니다.
        text = re.sub(r"\[([^\]]+)\]\((?!http|images/)[^)]*\.md[^)]*\)", r"\1", text)
        (OUT / dst).write_text(head + text, encoding="utf8")
        print("md  ", dst)

    # 순서도 문서(새로 작성): 세 장의 순서도와 설명
    flow = f"""# VisionQC 순서도

작성 2026-11-26 · 작성자 반재영 · 표준 순서도 기호(둥근 사각형=시작·끝, 사각형=처리, 마름모=판단, 평행사변형=입출력)

## 1. 개발 절차 순서도

![개발 절차 순서도](images/flow-dev.png)

계획·분석 → 설계 → 구현 → 테스트 → 유지보수의 순서로 진행하며, 테스트에서 결함이나 개선 사항이 나오면 구현 단계로 돌아가 수정합니다.

## 2. 검사 처리 순서도

![검사 처리 순서도](images/flow-inspection.png)

| 단계 | 설명 |
|---|---|
| 로그인 | 최초 실행이면 관리자 계정을 만듭니다. 로그인에 5번 실패하면 10분간 잠깁니다. |
| 설정 | 입력 소스(폴더·폴더 감시·영상/카메라), 모델 파일, 임계값, ROI를 정합니다. 모델이 열리지 않으면 검사 버튼을 막고 이유를 안내합니다. |
| 회차 시작 | 사용한 임계값과 모델 버전을 기록한 검사 회차를 만듭니다(나중에 같은 조건을 추적하기 위함). |
| 이미지 처리 반복 | 이미지를 대기열(가득 차면 읽기 대기)에서 하나씩 꺼내 전처리 → ONNX 추론 → 판정합니다. 점수가 유효하지 않거나 중간에 예외가 나면 ERROR로 기록하고 다음 이미지를 계속 처리합니다(판정 불능을 양품으로 처리하지 않음). |
| 판정 | 불량 점수가 임계값 이상이면 NG, 아니면 OK. |
| 저장·표시 | 원 판정을 SQLite에 저장하고(NG이고 CAM이 있으면 히트맵도 저장) 화면에 판정·누적 통계를 갱신합니다. |
| 종료 | 완료·중지·실패 상태와 OK·NG·ERROR 건수를 요약하고, 이후 이력·통계·재판정 화면에서 조회합니다. |

## 3. 재판정·권한 순서도

![재판정·권한 순서도](images/flow-review.png)

재판정(정정)은 엔지니어 이상만 할 수 있고, **원 판정은 절대 수정하지 않고** `result_review`에 정정 행을 추가합니다. 같은 결과에 정정이 여러 번 있으면 id가 가장 큰(나중) 정정이 유효하며, 모든 정정은 감사 로그에 남습니다. 권한은 화면에서 버튼을 숨기는 것에 그치지 않고 서버 쪽 로직에서도 검사합니다.
"""
    (OUT / "VisionQC_순서도.md").write_text(flow, encoding="utf8")
    print("md   VisionQC_순서도.md")

    # md → docx (이 저장소 밖의 공용 변환기를 쓰지 않도록 tools/docs/md2docx.py 를 함께 둡니다)
    conv = Path(__file__).with_name("md2docx.py")
    for md in sorted(OUT.glob("VisionQC_*.md")):
        subprocess.run([sys.executable, str(conv), str(md), str(md.with_suffix(".docx"))], check=True, stdout=subprocess.DEVNULL)
        print("docx", md.with_suffix(".docx").name)


if __name__ == "__main__":
    main()
