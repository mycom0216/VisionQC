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
| `training/` | PyTorch 학습·ONNX 내보내기·평가, MVTec AD 변환 ([README](training/README.md), [DATASET](training/DATASET.md)) |
| `docs/` | [분석서](docs/01_분석서.md), [설계서](docs/02_설계서.md), 화면 캡처 |

## 실행

```bash
dotnet test VisionQC.Tests
dotnet run --project VisionQC.App
# 인자: --folder <이미지폴더> --model <model.onnx> --threshold 0.5 --db <파일> --autorun --tab history
```

모델은 `training/`으로 직접 학습해 만든다(저장소에 포함하지 않음).

## 현재 상태 (정직하게)

- MVP: 폴더 입력 → 전처리 → ONNX 분류 → 판정 → SQLite 이력 → 검사·이력 화면.
- MVTec AD `metal_nut` 시험셋 50장(불량 14): 불량 13/14 검출, 오검출 0건 (임계값 0.678, 검증셋에서 선택). 시험셋이 작아 일반화 근거로 쓰기엔 부족.
- 미구현: 불량 위치 박스 표시, 통계 화면, 재판정, 로그인·권한, 카메라 입력.
- 데이터셋(MVTec AD)은 CC BY-NC-SA 4.0(비상업). 저장소에 이미지를 포함하지 않는다.
