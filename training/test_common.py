"""common.py 단위 테스트 (torch 불필요).  실행: python -m unittest test_common -v"""
import tempfile
import unittest
from pathlib import Path

import cv2
import numpy as np

import common


class SplitTests(unittest.TestCase):
    def _samples(self, n_good=20, n_def=10):
        return [(f"good/{i}.png", 0) for i in range(n_good)] + [(f"defect/{i}.png", 1) for i in range(n_def)]

    def test_split_is_disjoint_complete_and_stratified(self):
        s = self._samples()
        sp = common.stratified_split(s, 0.2, 0.2, seed=1)
        all_items = sp["train"] + sp["val"] + sp["test"]
        self.assertEqual(sorted(all_items), sorted(s))
        self.assertEqual(len(set(all_items)), len(s))
        for k in sp:
            self.assertTrue({l for _, l in sp[k]} == {0, 1}, f"{k}에 클래스가 빠짐")

    def test_split_deterministic_by_seed(self):
        s = self._samples()
        self.assertEqual(common.stratified_split(s, 0.2, 0.2, 7), common.stratified_split(s, 0.2, 0.2, 7))
        self.assertNotEqual(common.stratified_split(s, 0.2, 0.2, 7), common.stratified_split(s, 0.2, 0.2, 8))

    def test_too_few_images_raises(self):
        with self.assertRaises(ValueError):
            common.stratified_split(self._samples(20, 2), 0.2, 0.2, 1)

    def test_split_roundtrip_json(self):
        sp = common.stratified_split(self._samples(), 0.2, 0.2, 1)
        with tempfile.TemporaryDirectory() as d:
            common.save_split(sp, Path(d) / "s.json")
            self.assertEqual(common.load_split(Path(d) / "s.json"), sp)


class DatasetTests(unittest.TestCase):
    def test_class_order_is_good_then_defect_not_alphabetical(self):
        with tempfile.TemporaryDirectory() as d:
            for name in ("good", "defect"):
                (Path(d) / name).mkdir()
                cv2.imwrite(str(Path(d) / name / "a.png"), np.zeros((4, 4, 3), np.uint8))
            labels = dict(common.list_dataset(d))
            self.assertEqual(labels["good/a.png"], 0)      # 알파벳순이면 defect=0이 되어 판정이 뒤집힌다
            self.assertEqual(labels["defect/a.png"], 1)

    def test_missing_class_folder_raises(self):
        with tempfile.TemporaryDirectory() as d:
            (Path(d) / "good").mkdir()
            cv2.imwrite(str(Path(d) / "good" / "a.png"), np.zeros((4, 4, 3), np.uint8))
            with self.assertRaises(FileNotFoundError):
                common.list_dataset(d)


class ImageTests(unittest.TestCase):
    def test_red_image_is_rgb_chw_and_normalized(self):
        with tempfile.TemporaryDirectory() as d:
            p = Path(d) / "r.png"
            bgr = np.zeros((30, 40, 3), np.uint8); bgr[:, :, 2] = 255        # BGR 순수 빨강
            cv2.imwrite(str(p), bgr)
            x = common.load_image(p, 8)
        self.assertEqual(x.shape, (3, 8, 8))
        self.assertEqual(x.dtype, np.float32)
        np.testing.assert_allclose(x[0], (1 - 0.485) / 0.229, rtol=1e-5)    # R 평면
        np.testing.assert_allclose(x[1], (0 - 0.456) / 0.224, rtol=1e-5)    # G
        np.testing.assert_allclose(x[2], (0 - 0.406) / 0.225, rtol=1e-5)    # B

    def test_korean_path(self):
        with tempfile.TemporaryDirectory() as d:
            p = Path(d) / "한글.png"
            ok, buf = cv2.imencode(".png", np.full((6, 6, 3), 200, np.uint8))
            p.write_bytes(buf.tobytes())
            self.assertEqual(common.load_image(p, 4).shape, (3, 4, 4))

    def test_corrupt_file_raises(self):
        with tempfile.TemporaryDirectory() as d:
            p = Path(d) / "x.png"; p.write_bytes(b"\x01\x02\x03")
            with self.assertRaises(ValueError):
                common.load_uint8(p, 4)


class MetricTests(unittest.TestCase):
    def test_softmax_matches_csharp_formula(self):
        np.testing.assert_allclose(common.softmax_defect(np.array([[2.0, 2.0]])), [0.5])
        np.testing.assert_allclose(common.softmax_defect(np.array([[0.0, 1000.0]])), [1.0])   # 오버플로 없음

    def test_confusion_boundary_is_ng(self):
        m = common.confusion([1, 0], [0.5, 0.5], 0.5)      # 점수 == 임계값 → 둘 다 NG
        self.assertEqual((m["tp"], m["fp"], m["fn"], m["tn"]), (1, 1, 0, 0))

    def test_metrics_values(self):
        y = [1, 1, 1, 1, 0, 0, 0, 0]
        s = [0.9, 0.8, 0.7, 0.2, 0.6, 0.3, 0.2, 0.1]
        m = common.confusion(y, s, 0.5)
        self.assertEqual((m["tp"], m["fn"], m["fp"], m["tn"]), (3, 1, 1, 3))
        self.assertAlmostEqual(m["recall"], 0.75)
        self.assertAlmostEqual(m["precision"], 0.75)
        self.assertAlmostEqual(m["miss_rate"], 0.25)
        self.assertAlmostEqual(m["false_alarm_rate"], 0.25)

    def test_pick_threshold_meets_recall_with_highest_threshold(self):
        y = [1, 1, 1, 1, 0, 0, 0, 0]
        s = [0.9, 0.8, 0.7, 0.2, 0.6, 0.3, 0.2, 0.1]
        t = common.pick_threshold(y, s, min_recall=0.75)
        self.assertEqual(t, 0.7)                            # 0.7 이하면 불량 3/4을 잡음, 더 높이면 recall 감소
        self.assertGreaterEqual(common.confusion(y, s, t)["recall"], 0.75)
        t_all = common.pick_threshold(y, s, min_recall=1.0)
        self.assertEqual(t_all, 0.2)                        # 전부 잡으려면 오검출이 늘어남

    def test_pick_threshold_without_defects_raises(self):
        with self.assertRaises(ValueError):
            common.pick_threshold([0, 0], [0.1, 0.2], 0.9)


if __name__ == "__main__":
    unittest.main()
