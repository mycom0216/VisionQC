"""CAM 히트맵이 실제 불량 위치를 얼마나 맞히는지 MVTec AD의 정답 마스크(ground_truth)로 측정한다.

  python eval_cam.py --model out/model_cam.onnx --data data --src <압축 푼 MVTec 루트> --category metal_nut --split out/split.json

지표(양의 CAM = 불량 쪽 증거만 사용, 224px 입력 공간에서 비교):
  · 포인팅 게임 적중률: CAM 최댓값 칸(7×7 중 하나)이 정답 마스크와 겹치는 비율. 비교 기준(우연) = 마스크와 겹치는 칸의 비율 평균.
  · 마스크 안 질량 비율: 양의 CAM 총합 중 마스크 안에 있는 몫. 우연 기준 = 마스크 면적 비율. 둘의 비 = lift(1이면 위치 정보 없음).
시험셋(학습에 안 쓴 이미지)과 전체를 따로 보고하고 불량 종류별로도 나눈다. 칸이 32px라 해상도 자체가 거칠다는 점이 한계다.
"""
from __future__ import annotations

import argparse
import csv
import json
from pathlib import Path

import cv2
import numpy as np

import common


def load_mask(path: Path, size: int) -> np.ndarray:
    data = np.fromfile(str(path), dtype=np.uint8)
    m = cv2.imdecode(data, cv2.IMREAD_GRAYSCALE)
    if m is None:
        raise ValueError(f"마스크를 읽을 수 없음: {path}")
    m = cv2.resize(m, (size, size), interpolation=cv2.INTER_NEAREST)     # 모델 입력과 같은 방식(비율 유지 안 함)
    return m > 127


def cell_overlap_fraction(mask: np.ndarray, cells: int) -> tuple[np.ndarray, float]:
    """각 칸이 마스크와 겹치는지(칸 수준) 와 겹치는 칸의 비율."""
    size = mask.shape[0]
    step = size / cells
    ov = np.zeros((cells, cells), bool)
    for r in range(cells):
        for c in range(cells):
            ov[r, c] = mask[int(r * step):int((r + 1) * step), int(c * step):int((c + 1) * step)].any()
    return ov, float(ov.mean())


def evaluate(model: str, data: Path, src_root: Path, category: str, split_path: str | None, size: int) -> dict:
    import onnxruntime as ort
    sess = ort.InferenceSession(model, providers=["CPUExecutionProvider"])
    in_name = sess.get_inputs()[0].name
    out_names = [o.name for o in sess.get_outputs()]
    if "cam" not in out_names:
        raise ValueError("모델에 cam 출력이 없음(add_cam_output.py로 만든 모델을 사용)")

    test_files = set()
    if split_path:
        test_files = {rel for rel, _ in common.load_split(split_path)["test"]}

    rows = []
    with open(data / "manifest.csv", encoding="utf-8-sig") as fh:
        for r in csv.DictReader(fh):
            if r["label"] != "1":
                continue
            kind, rel = r["defect_kind"], r["file"]
            stem = Path(r["source"]).stem
            mask_path = src_root / category / "ground_truth" / kind / f"{stem}_mask.png"
            mask = load_mask(mask_path, size)
            if not mask.any():
                continue                                                  # 마스크가 비어 있으면 평가 불가
            x = common.load_image(data / rel, size)[None]
            cam = dict(zip(out_names, sess.run(None, {in_name: x})))["cam"][0, 0]
            pos = np.maximum(cam, 0)
            cells = cam.shape[0]
            if pos.sum() <= 0:                                            # 불량 쪽 증거가 전혀 없음(모델이 양품으로 봄)
                rows.append(dict(file=rel, kind=kind, test=rel in test_files, no_evidence=True, area=float(mask.mean())))
                continue
            up = cv2.resize(pos, (size, size), interpolation=cv2.INTER_LINEAR)
            ov, chance_cells = cell_overlap_fraction(mask, cells)
            peak = np.unravel_index(pos.argmax(), pos.shape)
            area = float(mask.mean())
            mass_in = float(up[mask].sum() / up.sum())
            rows.append(dict(file=rel, kind=kind, test=rel in test_files, no_evidence=False, area=area,
                             hit=bool(ov[peak]), chance_hit=chance_cells, mass_in=mass_in, lift=mass_in / area))

    def summarize(sel):
        n = len(sel)
        ev = [r for r in sel if not r["no_evidence"]]
        if not ev:
            return {"n": n, "with_evidence": 0}
        return {
            "n": n, "with_evidence": len(ev),
            "pointing_hit_rate": float(np.mean([r["hit"] for r in ev])),
            "pointing_chance": float(np.mean([r["chance_hit"] for r in ev])),
            "mass_in_mask": float(np.mean([r["mass_in"] for r in ev])),
            "mask_area": float(np.mean([r["area"] for r in ev])),
            "lift_mean": float(np.mean([r["lift"] for r in ev])),
            "lift_median": float(np.median([r["lift"] for r in ev])),
        }

    kinds = sorted({r["kind"] for r in rows})
    return {"all": summarize(rows), "test_only": summarize([r for r in rows if r["test"]]),
            "by_kind": {k: summarize([r for r in rows if r["kind"] == k]) for k in kinds}}


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", required=True)
    ap.add_argument("--data", required=True)
    ap.add_argument("--src", required=True)
    ap.add_argument("--category", required=True)
    ap.add_argument("--split")
    ap.add_argument("--size", type=int, default=224)
    ap.add_argument("--out")
    a = ap.parse_args()
    res = evaluate(a.model, Path(a.data), Path(a.src), a.category, a.split, a.size)
    text = json.dumps(res, ensure_ascii=False, indent=2)
    print(text)
    if a.out:
        Path(a.out).write_text(text, encoding="utf-8")


if __name__ == "__main__":
    main()
