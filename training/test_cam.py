"""add_cam_output 테스트 — torch 없이, onnx로 만든 작은 분류 그래프(Conv → GlobalAveragePool → Flatten → Gemm)로 검증.
실행: python -m unittest test_cam -v  (onnx, onnxruntime 필요)"""
import tempfile
import unittest
from pathlib import Path

import numpy as np
import onnx
from onnx import TensorProto, helper, numpy_helper

import add_cam_output


def tiny_classifier(path: Path, classes: int = 2, trans_b: int = 1, with_gap: bool = True) -> None:
    rng = np.random.default_rng(0)
    c = 8
    conv_w = rng.normal(size=(c, 3, 1, 1)).astype(np.float32)
    conv_b = rng.normal(size=(c,)).astype(np.float32)
    fc_w = rng.normal(size=(classes, c)).astype(np.float32)
    fc_b = rng.normal(size=(classes,)).astype(np.float32)
    w_init = fc_w if trans_b else fc_w.T.copy()
    nodes = [helper.make_node("Conv", ["input", "conv_w", "conv_b"], ["feat"], name="conv")]
    last = "feat"
    if with_gap:
        nodes.append(helper.make_node("GlobalAveragePool", ["feat"], ["gap"], name="gap"))
        nodes.append(helper.make_node("Flatten", ["gap"], ["flat"], axis=1, name="flat"))
        last = "flat"
    nodes.append(helper.make_node("Gemm", [last, "fc_w", "fc_b"], ["logits"], transB=trans_b, name="fc"))
    g = helper.make_graph(
        nodes, "tiny",
        [helper.make_tensor_value_info("input", TensorProto.FLOAT, ["N", 3, 7, 7])],
        [helper.make_tensor_value_info("logits", TensorProto.FLOAT, ["N", classes])],
        [numpy_helper.from_array(conv_w, "conv_w"), numpy_helper.from_array(conv_b, "conv_b"),
         numpy_helper.from_array(w_init, "fc_w"), numpy_helper.from_array(fc_b, "fc_b")])
    m = helper.make_model(g, opset_imports=[helper.make_opsetid("", 17)])
    m.ir_version = 9
    onnx.save(m, str(path))


class CamTests(unittest.TestCase):
    def test_logits_unchanged_and_identity_holds(self):
        with tempfile.TemporaryDirectory() as d:
            src, dst = Path(d) / "m.onnx", Path(d) / "m_cam.onnx"
            tiny_classifier(src)
            info = add_cam_output.build_cam_model(src, dst)
            rng = np.random.default_rng(1)
            xs = [rng.normal(size=(1, 3, 7, 7)).astype(np.float32) for _ in range(10)]
            r = add_cam_output.verify(src, dst, xs, info)
            self.assertEqual(r["max_logit_difference"], 0.0)                 # 원래 로짓은 한 비트도 안 바뀐다
            self.assertLess(r["max_identity_error"], 1e-4)                   # mean(CAM) = 로짓 차 − 편향 차
            self.assertEqual(r["cam_shape"], [1, 1, 7, 7])

    def test_transposed_weight_layout_is_handled(self):
        with tempfile.TemporaryDirectory() as d:
            src, dst = Path(d) / "m.onnx", Path(d) / "m_cam.onnx"
            tiny_classifier(src, trans_b=0)
            info = add_cam_output.build_cam_model(src, dst)
            xs = [np.random.default_rng(2).normal(size=(1, 3, 7, 7)).astype(np.float32)]
            self.assertLess(add_cam_output.verify(src, dst, xs, info)["max_identity_error"], 1e-4)

    def test_cam_localizes_a_planted_signal(self):
        """한쪽 구석에만 신호를 넣으면 CAM 최댓값이 그 칸에 있어야 한다(방향·부호가 맞는지)."""
        import onnxruntime as ort
        with tempfile.TemporaryDirectory() as d:
            src, dst = Path(d) / "m.onnx", Path(d) / "m_cam.onnx"
            tiny_classifier(src)
            add_cam_output.build_cam_model(src, dst)
            sess = ort.InferenceSession(str(dst), providers=["CPUExecutionProvider"])
            names = [o.name for o in sess.get_outputs()]
            x0 = np.zeros((1, 3, 7, 7), np.float32)
            x = x0.copy(); x[0, :, 2, 5] = 3.0                               # (행2, 열5)에만 신호
            cam_base = dict(zip(names, sess.run(None, {"input": x0})))["cam"][0, 0]
            cam = dict(zip(names, sess.run(None, {"input": x})))["cam"][0, 0]
            delta = cam - cam_base                                           # 신호가 바꾼 증거
            self.assertEqual(np.unravel_index(np.abs(delta).argmax(), delta.shape), (2, 5))
            self.assertAlmostEqual(float(np.abs(np.delete(delta.flatten(), 2 * 7 + 5)).max()), 0.0, places=5)   # 다른 칸은 그대로

    def test_unsupported_structures_are_rejected(self):
        with tempfile.TemporaryDirectory() as d:
            src = Path(d) / "m.onnx"
            tiny_classifier(src, with_gap=False)                             # GlobalAveragePool 없음
            with self.assertRaises(ValueError):
                add_cam_output.build_cam_model(src, Path(d) / "o.onnx")
            tiny_classifier(src, classes=3)                                  # 3클래스
            with self.assertRaises(ValueError):
                add_cam_output.build_cam_model(src, Path(d) / "o.onnx")

    def test_second_application_is_rejected(self):
        with tempfile.TemporaryDirectory() as d:
            src, dst = Path(d) / "m.onnx", Path(d) / "m_cam.onnx"
            tiny_classifier(src)
            add_cam_output.build_cam_model(src, dst)
            with self.assertRaises(ValueError):
                add_cam_output.build_cam_model(dst, Path(d) / "again.onnx")


if __name__ == "__main__":
    unittest.main()
