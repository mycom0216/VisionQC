"""VisionQC 순서도 이미지 생성: python tools/docs/make_flow.py
  → docs/submission/images/flow-dev.png        개발 절차 순서도
  → docs/submission/images/flow-inspection.png 검사 처리 순서도(로그인 → 설정 → 이미지별 검사 → 저장)
  → docs/submission/images/flow-review.png     재판정·권한·감사 순서도
표준 순서도 기호: 둥근 사각형=시작/끝, 사각형=처리, 마름모=판단, 평행사변형=입출력.
"""
from pathlib import Path
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.patches import FancyBboxPatch, Polygon

plt.rcParams["font.family"] = "Malgun Gothic"
OUT = Path(__file__).resolve().parent.parent.parent / "docs" / "submission" / "images"
OUT.mkdir(parents=True, exist_ok=True)
BRAND, GREEN, RED, ORANGE, INK = "#0f766e", "#0f9d6b", "#d92d20", "#d97706", "#1b2430"


class Flow:
    def __init__(self, w, h, y0=-1.2):
        self.fig, self.ax = plt.subplots(figsize=(w, h - y0 if y0 < 0 else h - y0))
        self.ax.set_xlim(-1.2, w + 0.8)
        self.ax.set_ylim(y0, h)
        self.ax.axis("off")
        self.nodes = {}

    def node(self, key, x, y, text, kind="proc", w=3.4, h=0.8):
        a = self.ax
        if kind == "term":
            a.add_patch(FancyBboxPatch((x - w / 2, y - h / 2), w, h, boxstyle="round,pad=0,rounding_size=0.3", fc=BRAND, ec=BRAND, zorder=3))
            col = "white"
        elif kind == "dec":
            hw, hh = w / 2 + 0.2, h / 2 + 0.25
            a.add_patch(Polygon([(x, y + hh), (x + hw, y), (x, y - hh), (x - hw, y)], fc="#fff7e6", ec=ORANGE, lw=1.8, zorder=3))
            col = INK
        elif kind == "io":
            s = 0.3
            a.add_patch(Polygon([(x - w / 2 + s, y + h / 2), (x + w / 2 + s, y + h / 2), (x + w / 2 - s, y - h / 2), (x - w / 2 - s, y - h / 2)], fc="#e6f4f1", ec=BRAND, lw=1.8, zorder=3))
            col = INK
        else:
            a.add_patch(FancyBboxPatch((x - w / 2, y - h / 2), w, h, boxstyle="round,pad=0,rounding_size=0.08", fc="white", ec=BRAND, lw=1.8, zorder=3))
            col = INK
        a.text(x, y, text, ha="center", va="center", fontsize=11, color=col, zorder=5, fontweight="bold" if kind == "term" else "normal")
        hh = (h / 2 + 0.25) if kind == "dec" else h / 2
        hw = (w / 2 + 0.2) if kind == "dec" else w / 2
        self.nodes[key] = (x, y, hw, hh)

    def pt(self, key, side):
        x, y, hw, hh = self.nodes[key]
        return {"t": (x, y + hh), "b": (x, y - hh), "l": (x - hw, y), "r": (x + hw, y)}[side]

    def arrow(self, a, sa, b, sb, label="", via=None, color="#475467"):
        p1, p2 = self.pt(a, sa), self.pt(b, sb)
        pts = [p1] + (via or []) + [p2]
        for i in range(len(pts) - 1):
            last = i == len(pts) - 2
            self.ax.annotate("", xy=pts[i + 1], xytext=pts[i], zorder=2,
                             arrowprops=dict(arrowstyle="-|>" if last else "-", color=color, lw=1.5, shrinkA=0, shrinkB=0))
        if label:
            mx, my = pts[0][0] + (pts[1][0] - pts[0][0]) * 0.35, pts[0][1] + (pts[1][1] - pts[0][1]) * 0.35
            self.ax.text(mx + 0.12, my + 0.08, label, fontsize=10, color=RED if label.startswith("아니") else GREEN, fontweight="bold", zorder=6,
                         bbox=dict(fc="white", ec="none", pad=1))

    def save(self, name, title):
        self.ax.text(-1.0, self.ax.get_ylim()[1] - 0.35, title, fontsize=16, fontweight="bold", color=INK)
        self.fig.savefig(OUT / name, dpi=110, bbox_inches="tight", facecolor="white")
        # 아래·위에 남는 흰 여백을 잘라 내 문서·슬라이드에서 그림이 최대한 크게 보이게 합니다.
        from PIL import Image, ImageChops
        im = Image.open(OUT / name).convert("RGB")
        box = ImageChops.difference(im, Image.new("RGB", im.size, "white")).getbbox()
        if box:
            pad = 20
            im.crop((max(0, box[0] - pad), max(0, box[1] - pad), min(im.width, box[2] + pad), min(im.height, box[3] + pad))).save(OUT / name)
        print("saved", OUT / name)


# ── 1) 개발 절차 순서도 ──────────────────────────────────────────────
f = Flow(9, 12.4)
f.node("s", 4.5, 11.3, "시작 (2026-11)", "term", 3.0, 0.7)
f.node("p1", 4.5, 10.1, "1 계획·분석\n채용공고 조사, 주제·범위 확정, 분석서", "proc", 5.4)
f.node("p2", 4.5, 8.8, "2 설계\n아키텍처(Core/Imaging/Inference/Data/App), ERD", "proc", 5.6)
f.node("p3", 4.5, 7.5, "3 구현\n파이프라인, ONNX 추론, SQLite, WPF 화면, 권한", "proc", 5.4)
f.node("p4", 4.5, 6.2, "4 테스트\nxUnit·pytest·화면 자동화, 모델 평가, 성능 측정", "proc", 5.6)
f.node("d1", 4.5, 4.7, "결함·개선 사항이\n남아 있는가?", "dec", 2.6, 0.7)
f.node("p5", 4.5, 3.2, "5 유지보수\n결함 수정, 운영·백업 가이드, 문서 갱신", "proc", 5.4)
f.node("p6", 4.5, 1.95, "포트폴리오·발표자료 정리, 시연", "proc", 4.4)
f.node("e", 4.5, 0.85, "완료", "term", 2.4, 0.7)
for a, b in [("s", "p1"), ("p1", "p2"), ("p2", "p3"), ("p3", "p4"), ("p4", "d1")]:
    f.arrow(a, "b", b, "t")
f.arrow("d1", "b", "p5", "t", "아니오")
f.arrow("d1", "r", "p3", "r", "예 → 구현으로 수정", via=[(7.9, 4.7), (7.9, 7.5)], color=RED)
f.arrow("p5", "b", "p6", "t"); f.arrow("p6", "b", "e", "t")
f.save("flow-dev.png", "개발 절차 순서도 (계획 → 설계 → 구현 → 테스트 → 유지보수)")

# ── 2) 검사 처리 순서도 (한 장, 2열) ──────────────────────────────────
g = Flow(11.2, 14.4, y0=-1.6)
RX = 7.6   # 오른쪽 열(이미지 1장 처리 반복)의 가운데 x
# 왼쪽 열: 시작 준비 → 회차 종료
g.node("s", 2.4, 12.7, "시작", "term", 2.0, 0.6)
g.node("lg", 2.4, 11.7, "앱 실행 → 로그인\n(최초 실행: 관리자 계정 생성)", "io", 3.4, 0.8)
g.node("d1", 2.4, 10.2, "로그인 성공?", "dec", 2.2, 0.6)
g.node("cf", 2.4, 8.7, "설정: 입력 소스·모델·임계값·ROI\n(폴더 / 폴더 감시 / 영상·카메라)", "proc", 4.0, 0.85)
g.node("d2", 2.4, 7.2, "모델 로드·검증\n통과?", "dec", 2.2, 0.65)
g.node("jb", 2.4, 5.7, "검사 회차(job) 시작\n임계값·모델 버전 기록", "proc", 3.6, 0.8)
g.node("rs", 2.4, 3.9, "회차 종료: 완료·중지·실패\n요약(OK·NG·ERROR 건수)", "proc", 3.7, 0.8)
g.node("hv", 2.4, 2.4, "이력·통계·재판정 화면에서\n조회·정정(별도 순서도)", "io", 3.5, 0.8)
g.node("e", 2.4, 1.0, "끝", "term", 1.8, 0.55)
# 오른쪽 열: 이미지 1장 처리 반복
g.node("im", RX, 11.7, "이미지 1장 읽기\n(대기열이 가득 차면 읽기 대기)", "io", 3.4, 0.8)
g.node("pp", RX, 10.2, "전처리: ROI 자르기 → 224px\n리사이즈 → RGB 정규화", "proc", 3.6, 0.8)
g.node("inf", RX, 8.8, "ONNX 추론: 불량 점수\n(+ CAM 히트맵)", "proc", 3.2, 0.75)
g.node("d3", RX, 7.3, "점수가 유효한가?\n(NaN·무한대 아님)", "dec", 2.5, 0.65)
g.node("d4", RX, 5.8, "점수 ≥ 임계값?", "dec", 2.3, 0.6)
g.node("sv", RX, 4.3, "SQLite 저장(원 판정 보존)\nNG + CAM이면 히트맵도 저장", "proc", 3.7, 0.8)
g.node("ui", RX, 2.9, "화면 갱신: 판정·오버레이·\n누적 통계(양품률)", "proc", 3.5, 0.8)
g.node("d5", RX, 1.5, "남은 이미지가 있는가?", "dec", 2.6, 0.6)
g.node("er", 10.7, 7.3, "ERROR\n(사유 기록)", "proc", 1.4, 0.8)
g.node("ng", 10.7, 5.8, "NG", "proc", 1.0, 0.55)
g.node("ok", 5.2, 5.8, "OK", "proc", 0.9, 0.55)

g.arrow("s", "b", "lg", "t")
g.arrow("lg", "b", "d1", "t")
g.arrow("d1", "b", "cf", "t", "예")
g.arrow("d1", "l", "lg", "l", "아니오", via=[(0.2, 10.2), (0.2, 11.7)])
g.ax.text(-0.15, 10.95, "5번 실패하면\n10분 잠금", fontsize=9.5, color=RED, ha="center", va="center", bbox=dict(fc="white", ec="none", pad=1), zorder=7)
g.arrow("cf", "b", "d2", "t")
g.arrow("d2", "b", "jb", "t", "예")
g.arrow("d2", "l", "cf", "l", "아니오", via=[(0.2, 7.2), (0.2, 8.7)])
g.ax.text(-0.15, 7.95, "오류 안내\n검사 버튼 비활성", fontsize=9.5, color=RED, ha="center", va="center", bbox=dict(fc="white", ec="none", pad=1), zorder=7)
g.arrow("jb", "r", "im", "l", via=[(4.65, 5.7), (4.65, 11.7)])
g.arrow("im", "b", "pp", "t"); g.arrow("pp", "b", "inf", "t"); g.arrow("inf", "b", "d3", "t")
g.arrow("d3", "b", "d4", "t", "예")
g.arrow("d3", "r", "er", "l", "아니오")
g.arrow("d4", "r", "ng", "l", "예")
g.arrow("d4", "l", "ok", "r", "아니오")
g.arrow("ng", "b", "sv", "r", via=[(10.7, 4.3)])
g.arrow("er", "r", "sv", "r", via=[(11.5, 7.3), (11.5, 4.3), (10.7, 4.3)])
g.arrow("ok", "b", "sv", "l", via=[(5.2, 4.3)])
g.arrow("sv", "b", "ui", "t"); g.arrow("ui", "b", "d5", "t")
g.arrow("d5", "r", "im", "r", "예", via=[(11.8, 1.5), (11.8, 11.7)], color=GREEN)
g.arrow("d5", "l", "rs", "r", "아니오", via=[(4.45, 1.5), (4.45, 3.9)])
g.arrow("rs", "b", "hv", "t"); g.arrow("hv", "b", "e", "t")
g.ax.text(10.7, 9.2, "전처리·추론·판정 중\n예외 발생 → ERROR로\n기록하고 다음 이미지 계속", fontsize=9, color=RED, ha="center", va="center", bbox=dict(fc="white", ec="none", pad=1), zorder=7)
g.save("flow-inspection.png", "VisionQC 검사 처리 순서도")

# ── 3) 재판정·권한 순서도 ──────────────────────────────────────────────
h = Flow(9, 12.4)
h.node("s", 4.5, 11.3, "시작: 이력 화면에서 결과 선택", "term", 4.4, 0.7)
h.node("d1", 4.5, 9.9, "역할이 엔지니어\n이상인가?", "dec", 2.6, 0.7)
h.node("no", 8.0, 9.9, "정정 버튼 숨김\n(서버 쪽에서도 권한 검사)", "proc", 3.2, 0.8)
h.node("in", 4.5, 8.3, "정정 판정(OK/NG)·메모 입력\n(ERROR로는 정정 불가, 메모 500자 이내)", "io", 4.8, 0.85)
h.node("d2", 4.5, 6.8, "입력이 올바른가?", "dec", 2.4, 0.6)
h.node("sv", 4.5, 5.3, "result_review에 정정 행 추가\n(원 판정은 절대 수정하지 않음)", "proc", 4.2, 0.8)
h.node("au", 4.5, 3.9, "감사 로그 기록(누가·언제·무엇을)", "proc", 4.2, 0.7)
h.node("st", 4.5, 2.6, "이력·통계에 최신 정정 반영\n(id가 가장 큰 정정이 유효)", "proc", 4.2, 0.8)
h.node("e", 4.5, 1.2, "끝", "term", 1.8, 0.6)
h.arrow("s", "b", "d1", "t")
h.arrow("d1", "b", "in", "t", "예")
h.arrow("d1", "r", "no", "l", "아니오")
h.arrow("no", "b", "e", "r", via=[(8.0, 1.2)])
h.arrow("in", "b", "d2", "t")
h.arrow("d2", "b", "sv", "t", "예")
h.arrow("d2", "l", "in", "l", "아니오", via=[(1.0, 6.8), (1.0, 8.3)])
h.ax.text(0.2, 7.55, "오류 안내 후\n다시 입력", fontsize=9.5, color=RED, ha="center", va="center", bbox=dict(fc="white", ec="none", pad=1), zorder=7)
h.arrow("sv", "b", "au", "t"); h.arrow("au", "b", "st", "t"); h.arrow("st", "b", "e", "t")
h.save("flow-review.png", "재판정·권한 순서도 (원 판정 보존)")
