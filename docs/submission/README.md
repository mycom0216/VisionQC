# VisionQC 제출 문서 묶음

원본 문서(`docs/01~09`)를 제출용으로 정리한 사본입니다. 원본의 mermaid 도표를 그림(PNG)으로 바꿔 넣어 Word·PDF에서도 보이게 했습니다.

| 문서 | 파일 |
|---|---|
| 요구사항 분석서 | `VisionQC_요구사항분석서` (.md · .docx · .pdf) |
| 요구사항 명세서 | `VisionQC_요구사항명세서` |
| 데이터정의서 (ERD 포함) | `VisionQC_데이터정의서` |
| 설계서 | `VisionQC_설계서` |
| 순서도 3종 (개발 절차 · 검사 처리 · 재판정·권한) | `VisionQC_순서도`, `images/flow-*.png` |
| 포트폴리오 | `VisionQC_포트폴리오` |
| 발표자료 | `VisionQC_발표자료.pptx` · `.pdf` (기존 18장 + 순서도 2장 + ERD 1장 = 21장) |

## 다시 만들기
```powershell
python tools/docs/make_flow.py     # 순서도 PNG
python tools/docs/build_docs.py    # md → docx (mermaid 교체)
powershell -ExecutionPolicy Bypass -File tools/docs/docx2pdf.ps1 docs/submission/VisionQC_순서도.docx   # docx → pdf (Word 필요)
python tools/docs/build_deck.py    # 발표자료 보강
powershell -ExecutionPolicy Bypass -File tools/docs/pptx2pdf.ps1 docs/submission/VisionQC_발표자료.pptx  # pptx → pdf (PowerPoint 필요)
```

## 주의
- ERD 이미지(`images/erd.png`)는 실제 구현 스키마(6개 테이블)에서 그린 것으로, 원본 `outputs/.../VisionQC_ERD.png`를 복사한 것입니다.
- 문서 안의 수치·상태는 원본 문서의 작성 시점(2026-11-13~17) 기준입니다.
