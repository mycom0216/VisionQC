"""분류 ONNX 모델에 "불량 위치 히트맵(CAM)" 출력을 덧붙인다 — 재학습·새 가중치 없이, 원래 로짓은 그대로.

  python add_cam_output.py --onnx out/model.onnx --out out/model_cam.onnx

방법(Class Activation Mapping): ResNet 계열은 마지막 합성곱 특징맵 F[c,h,w] → 전역 평균 풀링 → FC 이므로
  logit_k = Σ_c W[k,c] · mean_hw F[c] + b_k
따라서 위치별 증거 CAM[h,w] = Σ_c (W[1,c] − W[0,c]) · F[c,h,w] 를 만들면 "불량 로짓 − 양품 로짓"이 위치별로 분해된다:
  mean_hw CAM = (logit_1 − logit_0) − (b_1 − b_0)      ← 이 항등식으로 올바르게 만들어졌는지 검증한다.
그래프에 Mul + ReduceSum 노드만 추가하며 기존 출력(logits)은 바뀌지 않는다. 새 출력 이름은 "cam" (형상 [N,1,h,w], 224px 입력이면 7×7).

한계: 해상도가 낮아(7×7, 칸 하나 = 입력의 32px) "대략 이 부근"을 보여 줄 뿐 정확한 윤곽이 아니다.
구조 가정: GlobalAveragePool → (Flatten) → Gemm 으로 끝나는 분류 모델(torchvision ResNet18 내보내기). 다르면 오류로 중단한다.
"""
from __future__ import annotations

import argparse
from pathlib import Path

import numpy as np
import onnx
from onnx import TensorProto, helper, numpy_helper


def build_cam_model(src: str | Path, dst: str | Path) -> dict:
    model = onnx.load(str(src))
    g = model.graph

    gap = [n for n in g.node if n.op_type == "GlobalAveragePool"]
    gemm = [n for n in g.node if n.op_type == "Gemm"]
    if len(gap) != 1 or len(gemm) != 1:
        raise ValueError(f"지원하지 않는 구조: GlobalAveragePool {len(gap)}개, Gemm {len(gemm)}개 (각각 1개여야 함)")
    if any(o.name == "cam" for o in g.output):
        raise ValueError("이미 cam 출력이 있습니다.")

    features = gap[0].input[0]                              # 전역 평균 풀링 직전 특징맵 [N,C,h,w]
    inits = {i.name: i for i in g.initializer}
    w_name = gemm[0].input[1]
    if w_name not in inits:
        raise ValueError("Gemm 가중치를 초기값에서 찾을 수 없음")
    W = numpy_helper.to_array(inits[w_name]).astype(np.float32)
    trans_b = next((a.i for a in gemm[0].attribute if a.name == "transB"), 0)
    if trans_b == 0:
        W = W.T                                             # [in,out] → [out,in]
    if W.shape[0] != 2:
        raise ValueError(f"출력 클래스가 2개여야 함(현재 {W.shape[0]})")
    diff = (W[1] - W[0]).reshape(1, -1, 1, 1).astype(np.float32)     # [1,C,1,1] : 불량 − 양품

    g.initializer.append(numpy_helper.from_array(diff, "cam_w"))
    g.initializer.append(numpy_helper.from_array(np.array([1], dtype=np.int64), "cam_axes"))
    g.node.append(helper.make_node("Mul", [features, "cam_w"], ["cam_weighted"], name="cam_mul"))
    g.node.append(helper.make_node("ReduceSum", ["cam_weighted", "cam_axes"], ["cam"], keepdims=1, name="cam_sum"))
    g.output.append(helper.make_tensor_value_info("cam", TensorProto.FLOAT, ["N", 1, "cam_h", "cam_w_dim"]))   # [N,1,h,w]

    onnx.checker.check_model(model)
    onnx.save(model, str(dst))
    bias = None
    for name in gemm[0].input[2:3]:
        if name in inits:
            bias = numpy_helper.to_array(inits[name]).astype(np.float32)
    return {"features_tensor": features, "bias_diff": float(bias[1] - bias[0]) if bias is not None else 0.0}


def verify(original: str | Path, cam_model: str | Path, images: list[np.ndarray], info: dict) -> dict:
    """① logits가 원본과 완전히 같은지 ② mean(CAM) = (logit1 − logit0) − (b1 − b0) 항등식이 성립하는지."""
    import onnxruntime as ort
    a = ort.InferenceSession(str(original), providers=["CPUExecutionProvider"])
    b = ort.InferenceSession(str(cam_model), providers=["CPUExecutionProvider"])
    in_a, in_b = a.get_inputs()[0].name, b.get_inputs()[0].name
    out_names = [o.name for o in b.get_outputs()]
    logit_diff_max, identity_max = 0.0, 0.0
    for x in images:
        la = a.run(None, {in_a: x})[0]
        res = dict(zip(out_names, b.run(None, {in_b: x})))
        lb, cam = res[out_names[0]], res["cam"]
        logit_diff_max = max(logit_diff_max, float(np.abs(la - lb).max()))
        lhs = float(cam.mean())
        rhs = float(lb[0, 1] - lb[0, 0]) - info["bias_diff"]
        identity_max = max(identity_max, abs(lhs - rhs))
    return {"max_logit_difference": logit_diff_max, "max_identity_error": identity_max, "cam_shape": list(cam.shape)}


def main() -> None:
    import common
    ap = argparse.ArgumentParser()
    ap.add_argument("--onnx", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--data", help="검증에 쓸 이미지 폴더(data/). 주면 항등식을 실제 이미지로 확인")
    ap.add_argument("--size", type=int, default=224)
    a = ap.parse_args()
    info = build_cam_model(a.onnx, a.out)
    print(f"저장: {a.out}  (특징맵 텐서: {info['features_tensor']})")
    if a.data:
        files = sorted(Path(a.data).rglob("*.png"))[:20]
        xs = [common.load_image(f, a.size)[None] for f in files]
        r = verify(a.onnx, a.out, xs, info)
        print(f"검증({len(xs)}장): 로짓 최대 차이 {r['max_logit_difference']:.2e}, 항등식 최대 오차 {r['max_identity_error']:.2e}, CAM 형상 {r['cam_shape']}")


if __name__ == "__main__":
    main()
