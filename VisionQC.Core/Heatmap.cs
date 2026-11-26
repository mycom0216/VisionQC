// ============================================================================
// Heatmap.cs - 불량 위치 히트맵(CAM) 데이터와 보조 계산/저장 형식
//
// 파이프라인에서의 위치: ONNX 모델이 선택 출력 "cam" 을 내면(OnnxClassifier) 그것이 Heatmap 이 되어
// InferenceOutput -> InspectionResult 로 흘러가고, NG 결과일 때 이력(SQLite BLOB)에 직렬화되어 저장된다.
// UI 는 이 격자를 이미지 위에 겹쳐 "모델이 불량이라 본 대략적 위치"를 보여 준다(판정 근거 설명, 참고용).
// CAM(Class Activation Map) = 분류 모델이 클래스를 결정할 때 이미지의 어느 칸에 주목했는지를 칸별 수치로 나타낸 것.
// ============================================================================
namespace VisionQC.Core;

/// <summary>
/// 불량 위치 히트맵(CAM). Values는 부호 있는 위치별 증거(양수 = 불량 쪽, 음수 = 양품 쪽), 행 우선 Width×Height.
/// Region은 이 격자가 원본 이미지에서 덮는 영역(비율) — ROI를 쓰면 전체가 아니라 그 영역이다.
/// 해상도가 낮아(224px 입력이면 7×7, 칸 하나가 입력의 32px) "대략 이 부근"을 보여 주는 참고용이며 정확한 윤곽이 아니다.
/// </summary>
public sealed record Heatmap(int Width, int Height, float[] Values, double RegionX = 0, double RegionY = 0, double RegionW = 1, double RegionH = 1);

/// <summary>히트맵 보조 함수 모음: 정규화, 최고점 찾기, ROI 영역 표시, 바이너리 저장/복원.</summary>
public static class HeatmapMath
{
    // 저장 데이터 맨 앞의 4바이트 표식 "VHM1"(VisionQC HeatMap, 형식 버전 1). 다른 데이터를 히트맵으로 오인하지 않게 한다.
    private static readonly byte[] Magic = { (byte)'V', (byte)'H', (byte)'M', (byte)'1' };
    // 한 변 최대 칸 수. 손상·악의적 데이터로 거대한 배열을 할당하는 것을 막는 안전 상한이다.
    public const int MaxSide = 256;

    /// <summary>양의 증거만 0~1로 정규화(가장 강한 칸 = 1). 양의 증거가 없으면 모두 0.</summary>
    public static float[] Normalize(Heatmap h)
    {
        // 음수(양품 쪽 증거)와 NaN/무한대는 0으로 바꿔 "불량 쪽" 증거만 남긴다.
        var pos = h.Values.Select(v => float.IsFinite(v) && v > 0 ? v : 0f).ToArray();
        float max = pos.Length == 0 ? 0 : pos.Max();
        // 최댓값이 사실상 0이면 나눗셈이 의미 없으므로 전부 0(= 표시할 불량 위치 없음)으로 돌려준다.
        if (max <= 1e-9f) return new float[pos.Length];
        for (int i = 0; i < pos.Length; i++) pos[i] /= max;
        return pos;
    }

    /// <summary>가장 강한 칸의 (열, 행). 양의 증거가 없으면 null.</summary>
    public static (int X, int Y)? Peak(Heatmap h)
    {
        int best = -1; float bestV = 0;
        for (int i = 0; i < h.Values.Length; i++)
            if (float.IsFinite(h.Values[i]) && h.Values[i] > bestV) { bestV = h.Values[i]; best = i; }
        return best < 0 ? null : (best % h.Width, best / h.Width);
    }

    /// <summary>ROI가 있으면 히트맵이 덮는 영역을 그 ROI로 표시한다(원본 이미지 좌표계로 옮기기 위해).</summary>
    public static Heatmap? WithRegion(Heatmap? h, Roi? roi)
        => h is null || roi is null ? h : h with { RegionX = roi.X, RegionY = roi.Y, RegionW = roi.W, RegionH = roi.H };

    /// <summary>저장 형식: "VHM1" + int32 W + int32 H + float32×4(영역) + float32×(W·H), little-endian.</summary>
    public static byte[] Serialize(Heatmap h)
    {
        // 저장 전 검증: 크기 범위와 "값 개수 == 가로*세로"가 맞아야 복원 때 형식 오류가 나지 않는다.
        if (h.Width < 1 || h.Height < 1 || h.Width > MaxSide || h.Height > MaxSide || h.Values.Length != h.Width * h.Height)
            throw new ArgumentException("히트맵 크기가 올바르지 않습니다.", nameof(h));
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        // 헤더(표식, 크기, 덮는 영역 4개)를 쓴 뒤 값들을 순서대로 기록한다. BinaryWriter 는 little-endian.
        w.Write(Magic); w.Write(h.Width); w.Write(h.Height);
        w.Write((float)h.RegionX); w.Write((float)h.RegionY); w.Write((float)h.RegionW); w.Write((float)h.RegionH);
        foreach (var v in h.Values) w.Write(v);
        return ms.ToArray();
    }

    /// <summary>깨졌거나 형식이 다르면 null — 이력 화면이 잘못된 BLOB 때문에 죽지 않게 한다.</summary>
    public static Heatmap? TryDeserialize(byte[]? bytes)
    {
        try
        {
            // 4(표식)+8(가로·세로)+16(영역 float 4개) = 최소 헤더 28바이트. 이보다 짧으면 깨진 데이터.
            if (bytes is null || bytes.Length < 4 + 8 + 16) return null;
            using var r = new BinaryReader(new MemoryStream(bytes));
            if (!r.ReadBytes(4).SequenceEqual(Magic)) return null;
            int w = r.ReadInt32(), h = r.ReadInt32();
            if (w < 1 || h < 1 || w > MaxSide || h > MaxSide) return null;
            double rx = r.ReadSingle(), ry = r.ReadSingle(), rw = r.ReadSingle(), rh = r.ReadSingle();
            if (!double.IsFinite(rx + ry + rw + rh) || rw <= 0 || rh <= 0) return null;
            // 전체 길이가 헤더 + 값(float 4바이트 * 칸 수)과 정확히 같아야 한다. 어긋나면 잘린/오염된 데이터로 보고 버린다.
            if (bytes.Length != 4 + 8 + 16 + w * h * 4) return null;
            var values = new float[w * h];
            for (int i = 0; i < values.Length; i++) values[i] = r.ReadSingle();
            return values.All(float.IsFinite) ? new Heatmap(w, h, values, rx, ry, rw, rh) : null;
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ArgumentException) { return null; }
    }
}
