# training — 양품/불량 분류 모델 학습

ResNet18(ImageNet 사전학습 미세조정) → ONNX(logits 2개) → 지표 계산. 결과 `out/model.onnx`를 VisionQC 앱의 "모델(.onnx)"에서 고르면 된다.

## 준비

```bash
python -m venv .venv
.venv\Scripts\activate
pip install -r requirements.txt
```

데이터 폴더 (직접 준비. 공개 데이터셋은 **사용 전 라이선스 확인**):

```
data/good/...      양품 (하위 폴더 허용)
data/defect/...    불량
```

## 실행

```bash
python train.py --data data --out out --epochs 20
```

| 옵션 | 설명 |
|---|---|
| `--no-pretrained` | 사전학습 가중치를 내려받지 않음(오프라인). 정확도는 떨어질 수 있음 |
| `--no-flip` | 뒤집기 증강 끔 — 부품 방향이 의미 있으면(예: 방향성 마킹) 반드시 끌 것 |
| `--min-recall 0.98` | 검증셋 재현율이 이 값 이상이 되는 가장 높은 임계값을 권장값으로 제시 |
| `--size 224` | C# 앱의 `InspectionSettings.InputSize`와 반드시 같아야 함 |
| `--reuse-split` | 이전 `split.json` 그대로 재학습(분할 고정) |

산출물(`out/`): `model.onnx`, `split.json`(고정 분할), `report.json`(지표·추론시간), `model_card.json`(전처리·임계값·재현 정보).

## C# 앱과의 약속 (어기면 판정이 조용히 틀린다)

| 항목 | 값 |
|---|---|
| 클래스 | 0=양품(good), 1=불량(defect) — 폴더 알파벳순이 아니라 코드에서 고정 |
| 전처리 | 컬러 → 정사각 리사이즈(INTER_AREA) → RGB → /255 → ImageNet mean/std → CHW |
| 출력 | logits 2개 (softmax는 C#에서) |
| 판정 | 불량 확률 ≥ 임계값 → NG (경계값 포함) |

일치 확인: `python parity_cases.py ...` 출력을 `VISIONQC_PARITY_CASES`에, 모델 경로를 `VISIONQC_PARITY_MODEL`에 넣고 `dotnet test --filter OnnxParityTests`.

## 평가 원칙

- 분할은 클래스 비율을 유지한 train/val/test, seed 고정. 시험셋은 **학습·에폭 선택·임계값 선택에 쓰지 않는다**(최종 1회 평가).
- 최적 에폭은 검증 F1, 임계값은 검증셋 재현율 기준으로 정한다. 불량을 놓치는 비용이 크므로 재현율 우선.
- 시험셋이 작으면(수십 장) 지표가 크게 흔들린다. 불량 1장이 재현율을 수 %p 움직인다는 점을 감안해 읽을 것.
- 추론 시간은 ONNX Runtime CPU 모델 실행만 잰 값이다(전처리·UI 제외).

## 테스트

```bash
python -m unittest test_common -v
```
