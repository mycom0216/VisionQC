"""MVTec AD의 한 카테고리를 train.py가 읽는 형태(data/good, data/defect)로 변환한다.

MVTec AD 구조:
    <root>/<category>/train/good/*.png
    <root>/<category>/test/good/*.png
    <root>/<category>/test/<불량종류>/*.png     (예: broken_large, scratch ...)
    <root>/<category>/ground_truth/...         (픽셀 마스크 — 분류에는 사용하지 않음)

변환 규칙: good = train/good + test/good,  defect = test/<불량종류>/* (good 제외 모든 하위 폴더).
원본 train/test 구분은 버리고 train.py가 클래스 비율을 유지해 새로 분할한다(고정 seed, split.json 저장).
파일명에 원본 위치·불량종류를 남기고 manifest.csv로 추적한다.

  python prepare_mvtec.py --src mvtec_anomaly_detection --category metal_nut --out data

※ 데이터셋 라이선스: CC BY-NC-SA 4.0 (비상업적 이용, 출처 표기, 동일조건 공유).
   원본 이미지를 저장소에 올리거나 재배포하지 말 것. 포트폴리오에는 지표·화면 캡처 위주로 쓴다.
"""
from __future__ import annotations

import argparse
import csv
import shutil
from pathlib import Path

import common


def prepare(src: Path, category: str, out: Path, force: bool = False) -> dict:
    cat = src / category
    if not cat.is_dir():
        raise FileNotFoundError(f"카테고리 폴더가 없음: {cat}")
    if not (cat / "test").is_dir():
        raise FileNotFoundError(f"test 폴더가 없음: {cat / 'test'} (MVTec AD 구조가 아님)")
    if out.exists() and any(out.iterdir()) and not force:
        raise FileExistsError(f"출력 폴더가 비어 있지 않음: {out} (덮어쓰려면 --force)")

    rows: list[tuple[str, int, str, str]] = []   # (새 상대경로, 라벨, 불량종류, 원본 상대경로)
    plan: list[tuple[Path, Path]] = []

    def add(files: list[Path], label: int, kind: str, tag: str):
        cls = common.CLASSES[label]
        for i, f in enumerate(files):
            new = Path(cls) / f"{tag}_{i:04d}{f.suffix.lower()}"
            plan.append((f, out / new))
            rows.append((new.as_posix(), label, kind, f.relative_to(src).as_posix()))

    def imgs(d: Path) -> list[Path]:
        return sorted(p for p in d.iterdir() if p.suffix.lower() in common.IMAGE_EXTS) if d.is_dir() else []

    add(imgs(cat / "train" / "good"), 0, "good", "train_good")
    add(imgs(cat / "test" / "good"), 0, "good", "test_good")
    kinds = sorted(d.name for d in (cat / "test").iterdir() if d.is_dir() and d.name != "good")
    if not kinds:
        raise ValueError(f"불량 종류 폴더가 없음: {cat / 'test'}")
    for k in kinds:
        add(imgs(cat / "test" / k), 1, k, f"test_{k}")

    n_good = sum(1 for r in rows if r[1] == 0)
    n_def = len(rows) - n_good
    if n_good == 0 or n_def == 0:
        raise ValueError(f"한쪽 클래스가 비어 있음 (good {n_good}, defect {n_def})")

    for s, d in plan:
        d.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(s, d)
    with open(out / "manifest.csv", "w", newline="", encoding="utf-8-sig") as fh:
        w = csv.writer(fh)
        w.writerow(["file", "label", "defect_kind", "source"])
        w.writerows(rows)
    return {"category": category, "good": n_good, "defect": n_def, "defect_kinds": kinds}


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--src", required=True, help="압축을 푼 MVTec AD 루트(카테고리 폴더들이 있는 곳)")
    ap.add_argument("--category", required=True)
    ap.add_argument("--out", default="data")
    ap.add_argument("--force", action="store_true")
    a = ap.parse_args()
    info = prepare(Path(a.src), a.category, Path(a.out), a.force)
    print(f"[{info['category']}] good {info['good']}장, defect {info['defect']}장 (종류: {', '.join(info['defect_kinds'])}) → {a.out}")


if __name__ == "__main__":
    main()
