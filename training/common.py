"""학습·평가 공용 코드 (numpy + OpenCV만 사용 — torch 없이 단위 테스트 가능).

C# 앱(VisionQC.Imaging.OpenCvPreprocessor / VisionQC.Inference.OnnxClassifier)과 반드시 같아야 하는 약속:
  - 클래스 순서: 인덱스 0 = 양품(good), 인덱스 1 = 불량(defect)
  - 전처리: 컬러로 읽기 → 정사각 리사이즈(INTER_AREA, 비율 유지 안 함) → BGR→RGB → /255 → (x-mean)/std → CHW float32
  - 모델 출력: logits 2개 (softmax는 C#이 한다)
"""
from __future__ import annotations

import json
import random
from pathlib import Path

import cv2
import numpy as np

CLASSES = ("good", "defect")          # 0 = OK, 1 = 불량. 폴더 이름순(알파벳)으로 정하면 뒤집히므로 명시한다.
MEAN = np.array([0.485, 0.456, 0.406], dtype=np.float32)
STD = np.array([0.229, 0.224, 0.225], dtype=np.float32)
IMAGE_EXTS = {".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff"}


# ---------------------------------------------------------------- 데이터 목록·분할
def list_dataset(root: str | Path) -> list[tuple[str, int]]:
    """root/good, root/defect 아래(하위 폴더 포함)의 이미지를 (상대경로, 라벨)로 반환."""
    root = Path(root)
    samples: list[tuple[str, int]] = []
    for label, name in enumerate(CLASSES):
        folder = root / name
        if not folder.is_dir():
            raise FileNotFoundError(f"클래스 폴더가 없음: {folder} (필요: {', '.join(CLASSES)})")
        files = sorted(p for p in folder.rglob("*") if p.suffix.lower() in IMAGE_EXTS)
        if not files:
            raise ValueError(f"이미지가 없음: {folder}")
        samples += [(p.relative_to(root).as_posix(), label) for p in files]
    return samples


def stratified_split(samples, val_frac: float, test_frac: float, seed: int) -> dict[str, list[tuple[str, int]]]:
    """클래스 비율을 유지한 train/val/test 분할. 같은 seed면 항상 같은 결과.
    각 분할에 클래스가 최소 1장씩 들어가지 못하면 오류(평가가 무의미해지므로)."""
    if not (0 < val_frac < 1 and 0 < test_frac < 1 and val_frac + test_frac < 1):
        raise ValueError("val_frac/test_frac는 0~1 사이이고 합이 1 미만이어야 함")
    rng = random.Random(seed)
    out: dict[str, list] = {"train": [], "val": [], "test": []}
    for label in range(len(CLASSES)):
        items = sorted(s for s in samples if s[1] == label)
        rng.shuffle(items)
        n = len(items)
        n_test = max(1, round(n * test_frac))
        n_val = max(1, round(n * val_frac))
        if n - n_test - n_val < 1:
            raise ValueError(f"'{CLASSES[label]}' 클래스 이미지가 너무 적음({n}장). 최소 3장 이상 필요")
        out["test"] += items[:n_test]
        out["val"] += items[n_test:n_test + n_val]
        out["train"] += items[n_test + n_val:]
    for k in out:
        out[k].sort()
    return out


def save_split(split: dict, path: str | Path) -> None:
    Path(path).write_text(json.dumps(split, ensure_ascii=False, indent=1), encoding="utf-8")


def load_split(path: str | Path) -> dict[str, list[tuple[str, int]]]:
    raw = json.loads(Path(path).read_text(encoding="utf-8"))
    return {k: [(p, int(l)) for p, l in v] for k, v in raw.items()}


# ---------------------------------------------------------------- 이미지
def load_uint8(path: str | Path, size: int) -> np.ndarray:
    """디스크 → 리사이즈된 RGB uint8 (H,W,3). 한글 경로 OK(바이트로 읽어 디코드)."""
    data = np.fromfile(str(path), dtype=np.uint8)
    bgr = cv2.imdecode(data, cv2.IMREAD_COLOR) if data.size else None
    if bgr is None:
        raise ValueError(f"이미지를 읽을 수 없음: {path}")
    bgr = cv2.resize(bgr, (size, size), interpolation=cv2.INTER_AREA)
    return cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB)


def normalize_chw(rgb_u8: np.ndarray) -> np.ndarray:
    """RGB uint8 (H,W,3) → 정규화된 float32 (3,H,W). C# 전처리와 동일."""
    x = rgb_u8.astype(np.float32) / 255.0
    x = (x - MEAN) / STD
    return np.ascontiguousarray(x.transpose(2, 0, 1))


def load_image(path: str | Path, size: int) -> np.ndarray:
    return normalize_chw(load_uint8(path, size))


# ---------------------------------------------------------------- 지표
def softmax_defect(logits: np.ndarray) -> np.ndarray:
    """logits (N,2) → 불량 확률 (N,). C# OnnxClassifier.ToDefectProbability와 같은 식."""
    z = logits - logits.max(axis=1, keepdims=True)
    e = np.exp(z)
    return (e[:, 1] / e.sum(axis=1)).astype(np.float64)


def confusion(y_true, scores, threshold: float) -> dict:
    """score >= threshold 이면 불량(NG) 예측 — C# ThresholdJudge와 같은 규칙(경계값은 NG)."""
    y = np.asarray(y_true)
    pred = (np.asarray(scores) >= threshold).astype(int)
    tp = int(((pred == 1) & (y == 1)).sum())
    fn = int(((pred == 0) & (y == 1)).sum())
    fp = int(((pred == 1) & (y == 0)).sum())
    tn = int(((pred == 0) & (y == 0)).sum())
    recall = tp / (tp + fn) if tp + fn else float("nan")
    precision = tp / (tp + fp) if tp + fp else float("nan")
    f1 = (2 * precision * recall / (precision + recall)) if tp > 0 else 0.0
    fpr = fp / (fp + tn) if fp + tn else float("nan")
    return {"threshold": float(threshold), "tp": tp, "fn": fn, "fp": fp, "tn": tn,
            "recall": recall, "precision": precision, "f1": f1,
            "miss_rate": 1 - recall if recall == recall else float("nan"),   # 불량을 양품으로 놓친 비율
            "false_alarm_rate": fpr}                                         # 양품을 불량으로 판정한 비율


def pick_threshold(y_true, scores, min_recall: float) -> float:
    """검증셋에서 재현율(Recall) >= min_recall 을 만족하는 가장 높은 임계값.
    (높을수록 오검출이 줄어든다. 재현율은 임계값이 오를수록 단조 감소.)
    불량 샘플이 없으면 정할 수 없으므로 오류."""
    y = np.asarray(y_true)
    s = np.asarray(scores, dtype=np.float64)
    if (y == 1).sum() == 0:
        raise ValueError("검증셋에 불량 샘플이 없어 임계값을 정할 수 없음")
    best = None
    for t in sorted(set(s.tolist())):
        if confusion(y, s, t)["recall"] >= min_recall:
            best = t
    return float(best if best is not None else s.min())
