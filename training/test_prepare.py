"""prepare_mvtec.py 테스트 — 가짜 MVTec 구조로 검증.  실행: python -m unittest test_prepare -v"""
import csv
import tempfile
import unittest
from pathlib import Path

import cv2
import numpy as np

import common
import prepare_mvtec


def _img(p: Path):
    p.parent.mkdir(parents=True, exist_ok=True)
    cv2.imwrite(str(p), np.zeros((4, 4, 3), np.uint8))


def _fake(root: Path, cat="metal_nut"):
    for i in range(5): _img(root / cat / "train" / "good" / f"{i:03d}.png")
    for i in range(2): _img(root / cat / "test" / "good" / f"{i:03d}.png")
    for i in range(3): _img(root / cat / "test" / "scratch" / f"{i:03d}.png")
    for i in range(2): _img(root / cat / "test" / "bent" / f"{i:03d}.png")
    _img(root / cat / "ground_truth" / "scratch" / "000_mask.png")     # 마스크는 데이터에 섞이면 안 됨


class PrepareTests(unittest.TestCase):
    def test_converts_and_counts(self):
        with tempfile.TemporaryDirectory() as d:
            src, out = Path(d) / "src", Path(d) / "out"
            _fake(src)
            info = prepare_mvtec.prepare(src, "metal_nut", out)
            self.assertEqual((info["good"], info["defect"]), (7, 5))
            self.assertEqual(info["defect_kinds"], ["bent", "scratch"])
            self.assertEqual(len(list((out / "good").glob("*.png"))), 7)
            self.assertEqual(len(list((out / "defect").glob("*.png"))), 5)
            self.assertFalse(any("mask" in p.name for p in out.rglob("*.png")))   # ground_truth 제외

    def test_output_is_loadable_by_train_pipeline_with_correct_labels(self):
        with tempfile.TemporaryDirectory() as d:
            src, out = Path(d) / "src", Path(d) / "out"
            _fake(src)
            prepare_mvtec.prepare(src, "metal_nut", out)
            labels = dict(common.list_dataset(out))
            self.assertTrue(all(v == 0 for k, v in labels.items() if k.startswith("good/")))
            self.assertTrue(all(v == 1 for k, v in labels.items() if k.startswith("defect/")))

    def test_manifest_traces_source_and_kind(self):
        with tempfile.TemporaryDirectory() as d:
            src, out = Path(d) / "src", Path(d) / "out"
            _fake(src)
            prepare_mvtec.prepare(src, "metal_nut", out)
            rows = list(csv.DictReader(open(out / "manifest.csv", encoding="utf-8-sig")))
            self.assertEqual(len(rows), 12)
            r = next(x for x in rows if x["defect_kind"] == "scratch")
            self.assertEqual(r["label"], "1")
            self.assertIn("metal_nut/test/scratch/", r["source"])

    def test_refuses_nonempty_output_unless_forced(self):
        with tempfile.TemporaryDirectory() as d:
            src, out = Path(d) / "src", Path(d) / "out"
            _fake(src)
            prepare_mvtec.prepare(src, "metal_nut", out)
            with self.assertRaises(FileExistsError):
                prepare_mvtec.prepare(src, "metal_nut", out)
            prepare_mvtec.prepare(src, "metal_nut", out, force=True)

    def test_missing_category_raises(self):
        with tempfile.TemporaryDirectory() as d:
            with self.assertRaises(FileNotFoundError):
                prepare_mvtec.prepare(Path(d), "nope", Path(d) / "out")


if __name__ == "__main__":
    unittest.main()
