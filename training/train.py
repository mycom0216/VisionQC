"""양품/불량 분류 모델 학습 → ONNX 내보내기 → 지표 계산.

데이터 폴더 구조:
    data/good/...     양품 이미지
    data/defect/...   불량 이미지

실행 예:
    python train.py --data data --out out --epochs 20

산출물(out/): model.onnx, split.json(고정 분할), report.json(지표), model_card.json(재현 정보)
"""
from __future__ import annotations

import argparse
import copy
import hashlib
import json
import random
import time
from pathlib import Path

import numpy as np
import torch
import torch.nn as nn
from torch.utils.data import DataLoader, Dataset

import common
import evaluate as ev


class ImageDataset(Dataset):
    """이미지를 리사이즈된 uint8로 메모리에 올려 두고(장당 약 150KB), 매번 정규화·증강한다."""

    def __init__(self, root: Path, items, size: int, augment: bool, flip: bool):
        self.items, self.augment, self.flip = items, augment, flip
        self.images = [common.load_uint8(root / rel, size) for rel, _ in items]

    def __len__(self):
        return len(self.items)

    def __getitem__(self, i):
        img = self.images[i]
        if self.augment:
            if self.flip:
                if random.random() < 0.5:
                    img = img[:, ::-1]
                if random.random() < 0.5:
                    img = img[::-1, :]
            # 밝기·대비 약간 흔들기 (조명 변화 대응)
            a, b = random.uniform(0.85, 1.15), random.uniform(-15, 15)
            img = np.clip(img.astype(np.float32) * a + b, 0, 255).astype(np.uint8)
        return torch.from_numpy(common.normalize_chw(np.ascontiguousarray(img))), self.items[i][1]


def build_model(pretrained: bool) -> nn.Module:
    import torchvision
    weights = torchvision.models.ResNet18_Weights.DEFAULT if pretrained else None
    m = torchvision.models.resnet18(weights=weights)
    m.fc = nn.Linear(m.fc.in_features, len(common.CLASSES))   # 출력 = logits 2개 (0=양품, 1=불량)
    return m


@torch.no_grad()
def run_eval(model, loader, device, loss_fn):
    model.eval()
    total, n, logits_all, y_all = 0.0, 0, [], []
    for x, y in loader:
        x, y = x.to(device), y.to(device)
        out = model(x)
        total += loss_fn(out, y).item() * len(y)
        n += len(y)
        logits_all.append(out.cpu().numpy())
        y_all.append(y.cpu().numpy())
    scores = common.softmax_defect(np.concatenate(logits_all))
    return total / n, np.concatenate(y_all), scores


def export_onnx(model: nn.Module, path: Path, size: int) -> float:
    """logits를 내보내고, ONNX Runtime 결과가 PyTorch와 일치하는지 확인한다. 최대 오차 반환."""
    import onnxruntime as ort
    model = model.cpu().eval()
    dummy = torch.randn(1, 3, size, size)
    kwargs = dict(input_names=["input"], output_names=["logits"],
                  dynamic_axes={"input": {0: "batch"}, "logits": {0: "batch"}}, opset_version=17)
    try:
        torch.onnx.export(model, dummy, str(path), dynamo=False, **kwargs)   # 단일 파일 보장
    except TypeError:                                                        # 구버전 torch는 dynamo 인자가 없음
        torch.onnx.export(model, dummy, str(path), **kwargs)
    with torch.no_grad():
        ref = model(dummy).numpy()
    sess = ort.InferenceSession(str(path), providers=["CPUExecutionProvider"])
    got = sess.run(None, {"input": dummy.numpy()})[0]
    diff = float(np.abs(ref - got).max())
    if diff > 1e-3:
        raise RuntimeError(f"ONNX 결과가 PyTorch와 다름(최대 오차 {diff:.2e}) — 내보내기 실패")
    return diff


def sha256_12(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()[:12]   # C# OnnxClassifier의 모델 버전과 동일 규칙


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", required=True)
    ap.add_argument("--out", default="out")
    ap.add_argument("--epochs", type=int, default=20)
    ap.add_argument("--batch", type=int, default=32)
    ap.add_argument("--lr", type=float, default=3e-4)
    ap.add_argument("--size", type=int, default=224, help="C# 앱의 InspectionSettings.InputSize와 같아야 함")
    ap.add_argument("--seed", type=int, default=42)
    ap.add_argument("--val-frac", type=float, default=0.15)
    ap.add_argument("--test-frac", type=float, default=0.15)
    ap.add_argument("--min-recall", type=float, default=0.98, help="임계값 선택 기준(검증셋 재현율)")
    ap.add_argument("--no-pretrained", action="store_true", help="ImageNet 사전학습 가중치 사용 안 함(오프라인)")
    ap.add_argument("--no-flip", action="store_true", help="뒤집기 증강 끄기(방향이 의미 있는 부품일 때)")
    ap.add_argument("--reuse-split", action="store_true", help="out/split.json이 있으면 그대로 사용")
    ap.add_argument("--device", default="auto")
    a = ap.parse_args()

    random.seed(a.seed); np.random.seed(a.seed); torch.manual_seed(a.seed)
    device = torch.device("cuda" if a.device == "auto" and torch.cuda.is_available() else
                          "cpu" if a.device == "auto" else a.device)
    out = Path(a.out); out.mkdir(parents=True, exist_ok=True)
    root = Path(a.data)

    split_path = out / "split.json"
    if a.reuse_split and split_path.exists():
        split = common.load_split(split_path)
    else:
        split = common.stratified_split(common.list_dataset(root), a.val_frac, a.test_frac, a.seed)
        common.save_split(split, split_path)
    counts = {k: [sum(1 for _, l in v if l == c) for c in range(len(common.CLASSES))] for k, v in split.items()}
    print(f"분할(good, defect): {counts}  device={device}")

    flip = not a.no_flip
    ds = {k: ImageDataset(root, split[k], a.size, augment=(k == "train"), flip=flip) for k in split}
    train_dl = DataLoader(ds["train"], batch_size=a.batch, shuffle=True)
    val_dl = DataLoader(ds["val"], batch_size=a.batch)

    # 클래스 불균형: 학습셋 빈도의 역수로 가중 (불량이 적으면 불량 오류를 더 크게 벌점)
    n_cls = np.array(counts["train"], dtype=np.float32)
    weights = torch.tensor(n_cls.sum() / (len(n_cls) * n_cls), dtype=torch.float32).to(device)
    loss_fn = nn.CrossEntropyLoss(weight=weights)

    model = build_model(pretrained=not a.no_pretrained).to(device)
    opt = torch.optim.AdamW(model.parameters(), lr=a.lr, weight_decay=1e-4)
    sched = torch.optim.lr_scheduler.CosineAnnealingLR(opt, T_max=a.epochs)

    best_f1, best_state, best_epoch, history = -1.0, None, 0, []
    t0 = time.time()
    for epoch in range(1, a.epochs + 1):
        model.train()
        run, n = 0.0, 0
        for x, y in train_dl:
            x, y = x.to(device), y.to(device)
            opt.zero_grad()
            loss = loss_fn(model(x), y)
            loss.backward()
            opt.step()
            run += loss.item() * len(y); n += len(y)
        sched.step()

        vloss, yv, sv = run_eval(model, val_dl, device, loss_fn)
        m = common.confusion(yv, sv, 0.5)
        history.append({"epoch": epoch, "train_loss": run / n, "val_loss": vloss, "val_f1": m["f1"], "val_recall": m["recall"]})
        print(f"[{epoch:3d}/{a.epochs}] train {run / n:.4f}  val {vloss:.4f}  F1 {m['f1']:.3f}  recall {m['recall']:.3f}")
        # 검증 F1로 최적 에폭 선택(동률이면 더 이른 에폭 유지). 시험셋은 여기서 쓰지 않는다.
        if m["f1"] > best_f1:
            best_f1, best_epoch, best_state = m["f1"], epoch, copy.deepcopy(model.state_dict())

    model.load_state_dict(best_state)
    onnx_path = out / "model.onnx"
    diff = export_onnx(model, onnx_path, a.size)
    print(f"ONNX 내보내기 완료: {onnx_path}  (PyTorch 대비 최대 오차 {diff:.2e}, 최적 에폭 {best_epoch})")

    report = ev.evaluate(str(onnx_path), str(root), split, a.size, a.min_recall)
    (out / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")

    card = {
        "model_file": "model.onnx", "sha256_12": sha256_12(onnx_path),
        "task": "classification", "classes": list(common.CLASSES), "input": [1, 3, a.size, a.size],
        "output": "logits[2] (softmax 후 index1 = 불량 확률)",
        "preprocess": {"resize": "square, INTER_AREA", "color": "RGB", "scale": "x/255",
                       "mean": common.MEAN.tolist(), "std": common.STD.tolist()},
        "recommended_threshold": report["threshold_chosen_on_val"],
        "training": {"arch": "resnet18", "pretrained": not a.no_pretrained, "epochs": a.epochs, "best_epoch": best_epoch,
                     "batch": a.batch, "lr": a.lr, "seed": a.seed, "flip_augment": flip, "device": str(device),
                     "split_counts_good_defect": counts, "seconds": round(time.time() - t0, 1)},
        "torch": torch.__version__,
        "history": history,
        "caveat": "시험셋 지표는 이 데이터셋·분할에서의 값이다. 다른 조명·카메라·제품에서의 성능을 보장하지 않는다.",
    }
    (out / "model_card.json").write_text(json.dumps(card, ensure_ascii=False, indent=2), encoding="utf-8")

    t = report["test_at_threshold"]
    print(f"\n[시험셋 n={sum(counts['test'])}] 임계값 {report['threshold_chosen_on_val']:.4f} (검증셋 재현율>={a.min_recall})")
    print(f"  재현율 {t['recall']:.3f}  정밀도 {t['precision']:.3f}  F1 {t['f1']:.3f}  놓친 불량 {t['fn']}건  오검출 {t['fp']}건")
    print(f"  CPU 추론 평균 {report['inference_ms_cpu']['mean']:.1f} ms / p95 {report['inference_ms_cpu']['p95']:.1f} ms")
    print(f"산출물: {out.resolve()}")


if __name__ == "__main__":
    main()
