"""파이썬 쪽 점수를 뽑아 C# 일치 테스트(VisionQC.Tests/OnnxParityTests)에 넘길 문자열을 만든다.

  python parity_cases.py --onnx out/model.onnx --split out/split.json --data data --n 6

출력(마지막 줄)을 환경변수 VISIONQC_PARITY_CASES 로 설정하고, VISIONQC_PARITY_MODEL 에 model.onnx 경로를 지정한 뒤
  dotnet test --filter OnnxParityTests
"""
import argparse
from pathlib import Path

import numpy as np

import common
import evaluate as ev

ap = argparse.ArgumentParser()
ap.add_argument("--onnx", required=True)
ap.add_argument("--split", required=True)
ap.add_argument("--data", required=True)
ap.add_argument("--size", type=int, default=224)
ap.add_argument("--n", type=int, default=6, help="시험셋에서 양품·불량 번갈아 n장")
a = ap.parse_args()

test = common.load_split(a.split)["test"]
good = [t for t in test if t[1] == 0]
bad = [t for t in test if t[1] == 1]
pick = [x for pair in zip(good, bad) for x in pair][: a.n]
_, scores, _ = ev.predict_scores(a.onnx, a.data, pick, a.size)
print(";".join(f"{(Path(a.data) / rel).resolve()}|{s:.6f}" for (rel, _), s in zip(pick, scores)))
