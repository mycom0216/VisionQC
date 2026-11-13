# VisionQC — AI 머신비전 외관 불량검사 (C# WPF)

이미지를 AI(ONNX)로 검사해 양품/불량을 판정하고 이력을 저장·조회하는 WPF 데스크톱 프로그램.
채용공고(C#·WPF·딥러닝 외관검사 신입 직무)를 분석해 요구 기술을 한 프로젝트에 담았다.

## 구성

| 폴더 | 내용 |
|---|---|
| `VisionQC.Core` | 모델·인터페이스, 임계값 판정, 검사 파이프라인(생산자-소비자) |
| `VisionQC.Imaging` | 폴더 이미지 공급, OpenCvSharp 전처리 |
| `VisionQC.Inference` | ONNX 분류 모델 실행 |
| `VisionQC.Data` | SQLite 이력 저장·조회 |
| `VisionQC.App` | WPF 검사·이력 화면 |
| `VisionQC.Tests` | xUnit 테스트 |
| `VisionQC.Bench` | 성능·메모리 측정 도구(`dotnet run -c Release --project VisionQC.Bench -- --folder … --model …`) |
| `tools/` | 배포 스크립트 `publish.ps1` |
| `training/` | PyTorch 학습·ONNX 내보내기·평가, MVTec AD 변환 ([README](training/README.md), [DATASET](training/DATASET.md)) |
| `docs/` | [분석서](docs/01_분석서.md) · [설계서](docs/02_설계서.md) · [요구사항명세서](docs/03_요구사항명세서.md) · [데이터정의서](docs/04_데이터정의서.md) · `05_개발일정표.xlsx` · [포트폴리오](docs/06_포트폴리오.md) · [성능 측정](docs/07_성능측정결과.md) · [운영·유지보수 가이드](docs/08_운영유지보수가이드.md) · 화면 캡처 |

## 실행

```bash
dotnet test VisionQC.Tests
dotnet run --project VisionQC.App
# 인자(이번 실행에만 적용, 저장 안 됨): --folder <이미지폴더> --model <model.onnx> --threshold 0.5
#       --db <파일> --settings <settings.json> --backup-dir <폴더> --autorun --tab history|review|stats|settings|admin
```

배포 폴더 만들기: `powershell -ExecutionPolicy Bypass -File tools\publish.ps1` (대상 PC에 .NET 10 Desktop Runtime 필요). 설치·백업·모델 교체·장애 대응은 [운영·유지보수 가이드](docs/08_운영유지보수가이드.md).

- 처음 실행하면 **관리자 계정 만들기** 화면이 뜬다(비밀번호 8자 이상, 아이디 포함 불가). 이후 관리 탭에서 작업자·엔지니어 계정을 만든다. 비밀번호는 실행 인자로 받지 않는다.
- 역할: 작업자(검사·이력·통계) / 엔지니어(+재판정·설정) / 관리자(+사용자 관리·감사 로그).
- 모델은 `training/`으로 직접 학습해 만든다(저장소에 포함하지 않음). 설정 탭에서 모델·임계값·ROI를 지정한다.

## 현재 상태 (정직하게)

- MVP: 폴더 입력 → 전처리 → ONNX 분류 → 판정 → SQLite 이력 → 검사·이력·재판정(정정·임계값 미리보기)·통계·설정(모델·임계값·ROI)·관리(사용자·감사 로그) 화면, 로그인·3역할 권한·감사 로그.
- MVTec AD `metal_nut` 시험셋 50장(불량 14): 불량 13/14 검출, 오검출 0건 (임계값 0.678, 검증셋에서 선택). 시험셋이 작아 일반화 근거로 쓰기엔 부족.
- 연속 검사(폴더 감시), 무조작 자동 로그아웃(검사·감시 중 제외), DB 백업(수동) 구현. 성능은 이 PC 기준 장당 평균 20.5ms·p95 22.9ms, 2,000장 동안 메모리 증가 없음([측정 결과](docs/07_성능측정결과.md)).
- 미구현: 불량 위치 박스 표시(검출 모델), 카메라 입력, 자동 예약 백업·앱 내 복원, 인스톨러·코드 서명, 비밀번호 자체 복구. (통계의 불량 유형은 검출 모델 전환 후 표시)
- ROI는 학습 때와 같은 영역으로 학습한 모델이어야 하며, 현재 모델(전체 이미지 학습)로는 ROI를 켠 검증을 하지 않았다.
- 데이터셋(MVTec AD)은 CC BY-NC-SA 4.0(비상업). 저장소에 이미지를 포함하지 않는다.
