"""내보낸 ONNX 모델(= 실제로 C# 앱이 쓰는 파일)로 검증·시험 지표를 계산한다.

  python evaluate.py --onnx out/model.onnx --data data --split out/split.json --min-recall 0.98

임계값은 '검증셋'에서만 정하고, 시험셋은 그 값으로 한 번 평가한다(시험셋으로 임계값을 고르지 않는다).
"""
from __future__ import annotations

import argparse
import json
import time
from pathlib import Path

import numpy as np

import common


def predict_scores(onnx_path: str, data_root: str, items, size: int) -> tuple[np.ndarray, np.ndarray, list[float]]:
    """(라벨, 불량확률, 장당 추론 ms). 전처리 시간은 제외, 모델 실행 시간만 잰다."""
    import onnxruntime as ort
    sess = ort.InferenceSession(onnx_path, providers=["CPUExecutionProvider"])
    name = sess.get_inputs()[0].name
    labels, scores, times = [], [], []
    for rel, label in items:
        x = common.load_image(Path(data_root) / rel, size)[None]
        t0 = time.perf_counter()
        logits = sess.run(None, {name: x})[0]
        times.append((time.perf_counter() - t0) * 1000)
        labels.append(label)
        scores.append(common.softmax_defect(np.asarray(logits).reshape(1, -1))[0])
    return np.array(labels), np.array(scores), times


def evaluate(onnx_path: str, data_root: str, split: dict, size: int, min_recall: float) -> dict:
    yv, sv, _ = predict_scores(onnx_path, data_root, split["val"], size)
    thr = common.pick_threshold(yv, sv, min_recall)
    yt, st, ms = predict_scores(onnx_path, data_root, split["test"], size)
    return {
        "threshold_chosen_on_val": thr,
        "min_recall_target": min_recall,
        "val_at_threshold": common.confusion(yv, sv, thr),
        "test_at_threshold": common.confusion(yt, st, thr),
        "test_at_0.5": common.confusion(yt, st, 0.5),
        "n": {k: len(v) for k, v in split.items()},
        "inference_ms_cpu": {"mean": float(np.mean(ms)), "p95": float(np.percentile(ms, 95)), "images": len(ms),
                             "note": "ONNX Runtime CPU, 모델 실행만(전처리·UI 제외), 첫 호출 포함"},
    }


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--onnx", required=True)
    ap.add_argument("--data", required=True)
    ap.add_argument("--split", required=True)
    ap.add_argument("--size", type=int, default=224)
    ap.add_argument("--min-recall", type=float, default=0.98)
    ap.add_argument("--out", help="결과 JSON 저장 경로")
    a = ap.parse_args()

    rep = evaluate(a.onnx, a.data, common.load_split(a.split), a.size, a.min_recall)
    text = json.dumps(rep, ensure_ascii=False, indent=2)
    print(text)
    if a.out:
        Path(a.out).write_text(text, encoding="utf-8")


if __name__ == "__main__":
    main()
