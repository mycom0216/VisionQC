// ============================================================================
// Settings.cs - 앱 설정(관심 영역 ROI, 임계값, 모델 경로 등)의 모델과 파일 저장/불러오기
//
// 파이프라인에서의 위치: 설정(AppSettings)은 검사 시작 때 InspectionSettings 로 변환되어(ToInspectionSettings)
// 전처리(ROI 자르기)와 판정(임계값)에 전달된다. 설정은 JSON 파일로 저장한다.
// 설계 이유: 설정 파일은 사람이 손으로 고치거나 깨질 수 있으므로, 불러올 때 값을 검증·보정해 "앱이 죽지 않고 안전한 값으로 시작"하게 하고
// 저장할 때는 엄격히 검증해 잘못된 값이 파일에 들어가지 않게 한다.
// ============================================================================
using System.Text.Json;

namespace VisionQC.Core;

/// <summary>관심 영역(ROI). 이미지 크기에 대한 비율(0~1)이라 해상도가 달라도 같은 영역을 가리킨다.</summary>
// ROI(Region Of Interest, 관심 영역) = 이미지 전체가 아니라 검사할 부분만 잘라 모델에 넣는 영역.
// 배경의 불필요한 부분을 제거해 정확도를 높이는 용도다. 픽셀이 아닌 "비율"로 저장해 카메라 해상도가 바뀌어도 같은 위치를 가리킨다.
// X, Y = 왼쪽 위 시작점, W, H = 폭·높이(모두 이미지 크기 대비 0~1).
public sealed record Roi(double X, double Y, double W, double H)
{
    // 너무 작은 영역(5% 미만)은 모델에 넣을 정보가 거의 없고 실수로 만든 경우가 많아 거부한다.
    public const double MinSize = 0.05;   // 폭·높이 최소 5%

    /// <summary>문제가 있으면 사유, 없으면 null.</summary>
    public string? Validate()
    {
        // 검증 순서: 유한한 값인지 -> 시작점이 음수가 아닌지 -> 최소 크기 -> 이미지 범위 안인지.
        // 범위 검사의 1e-9 여유는 0.7 + 0.3 같은 부동소수점 오차 때문에 정상 영역이 거부되지 않게 하기 위함이다.
        if (!double.IsFinite(X) || !double.IsFinite(Y) || !double.IsFinite(W) || !double.IsFinite(H)) return "ROI 값이 유효하지 않습니다.";
        if (X < 0 || Y < 0) return "ROI의 시작 위치는 0 이상이어야 합니다.";
        if (W < MinSize || H < MinSize) return $"ROI의 폭·높이는 {MinSize:P0} 이상이어야 합니다.";
        if (X + W > 1.0 + 1e-9 || Y + H > 1.0 + 1e-9) return "ROI가 이미지 범위를 벗어납니다.";
        return null;
    }

    /// <summary>비율 ROI를 픽셀 사각형으로. 항상 이미지 안에 있고 폭·높이는 최소 1픽셀.</summary>
    public (int X, int Y, int W, int H) ToPixels(int imageWidth, int imageHeight)
    {
        // 반올림한 뒤 Clamp 로 범위를 강제한다: 시작점은 [0, 크기-1], 폭·높이는 [1, 남은 크기].
        // 이렇게 하면 반올림 오차가 있어도 항상 이미지 안의 최소 1픽셀 사각형이 되어 잘라내기에서 예외가 나지 않는다.
        int x = Math.Clamp((int)Math.Round(X * imageWidth), 0, imageWidth - 1);
        int y = Math.Clamp((int)Math.Round(Y * imageHeight), 0, imageHeight - 1);
        int w = Math.Clamp((int)Math.Round(W * imageWidth), 1, imageWidth - x);
        int h = Math.Clamp((int)Math.Round(H * imageHeight), 1, imageHeight - y);
        return (x, y, w, h);
    }
}

/// <summary>
/// 앱 설정. FolderPath는 마지막으로 쓴 폴더(누구나), 나머지(모델·임계값·ROI)는 엔지니어 이상만 바꾼다.
/// 주의: ROI를 쓰면 학습 때와 같은 영역을 잘라 학습한 모델이어야 한다.
/// </summary>
// 속성 의미: FolderPath = 마지막 검사 폴더, ModelPath = 사용 모델(.onnx), Threshold = 불량 판정 임계값(0~1), Roi = 관심 영역(없으면 전체).
public sealed record AppSettings
{
    public string FolderPath { get; init; } = "";
    public string ModelPath { get; init; } = "";
    public double Threshold { get; init; } = 0.5;
    public Roi? Roi { get; init; }
    /// <summary>무조작 자동 로그아웃(분, 1~480). 검사·감시 중에는 적용하지 않는다. 끌 수는 없다(보안 기본값 15분).</summary>
    public int IdleTimeoutMinutes { get; init; } = IdleMonitor.DefaultMinutes;
    /// <summary>자동 백업 주기(시간, 0=끔, 최대 720). 앱이 켜져 있고 누군가 로그인해 있을 때만 동작한다(서비스가 아님).</summary>
    public int AutoBackupHours { get; init; } = DefaultAutoBackupHours;

    // 기본 24시간마다, 최대 720시간(30일). 0은 자동 백업 끔.
    public const int DefaultAutoBackupHours = 24, MaxAutoBackupHours = 720;
    public static bool IsValidAutoBackupHours(int h) => h >= 0 && h <= MaxAutoBackupHours;

    /// <summary>앱 설정을 파이프라인이 쓰는 InspectionSettings 로 변환한다(임계값은 double -> float).</summary>
    public InspectionSettings ToInspectionSettings() => new() { Threshold = (float)Threshold, Roi = Roi };
}

/// <summary>설정 JSON 파일의 읽기/쓰기 담당. 읽기는 관대하게(보정), 쓰기는 엄격하게(검증) 처리한다.</summary>
public sealed class SettingsStore
{
    // JSON 옵션: 사람이 읽기 쉽게 들여쓰기, 속성명은 camelCase, 한글 경로가 유니코드 이스케이프(백슬래시u) 로 깨져 보이지 않게 이스케이프를 완화한다.
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping   // 한글 경로를 \uXXXX로 바꾸지 않음
    };

    /// <summary>설정 JSON 파일 경로.</summary>
    public string Path { get; }
    public SettingsStore(string path) => Path = path;

    /// <summary>파일이 없으면 기본값. 깨졌거나 값이 잘못됐으면 기본값/보정값으로 시작하고 사유를 돌려준다(앱이 죽지 않는다).</summary>
    public (AppSettings Settings, string? Warning) Load()
    {
        // 파일이 없으면(첫 실행) 기본 설정. 이후 단계마다 문제가 있는 값만 골라 보정하고 경고 문구를 모아 한꺼번에 돌려준다.
        if (!File.Exists(Path)) return (new AppSettings(), null);
        try
        {
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), Json);
            if (s is null) return (new AppSettings(), "설정 파일이 비어 있어 기본값을 사용합니다.");

            var warnings = new List<string>();
            var threshold = s.Threshold;
            // 임계값이 범위(0~1) 밖이면 가까운 끝값으로 보정, NaN/무한대면 기본값 0.5. 확률(0~1)과 비교하는 값이라 이 범위만 의미가 있다.
            if (!double.IsFinite(threshold) || threshold < 0 || threshold > 1)
            {
                warnings.Add("임계값이 범위를 벗어나 보정했습니다.");
                threshold = double.IsFinite(threshold) ? Math.Clamp(threshold, 0, 1) : 0.5;
            }
            var roi = s.Roi;
            if (roi is not null && roi.Validate() is { } problem)
            {
                warnings.Add($"ROI가 올바르지 않아 사용하지 않습니다({problem})");
                roi = null;
            }
            var idle = s.IdleTimeoutMinutes;
            if (!IdleMonitor.IsValidMinutes(idle))
            {
                warnings.Add($"자동 로그아웃 시간이 범위({IdleMonitor.MinMinutes}~{IdleMonitor.MaxMinutes}분)를 벗어나 {IdleMonitor.DefaultMinutes}분으로 되돌렸습니다.");
                idle = IdleMonitor.DefaultMinutes;
            }
            var autoBackup = s.AutoBackupHours;
            if (!AppSettings.IsValidAutoBackupHours(autoBackup))
            {
                warnings.Add($"자동 백업 주기가 범위(0~{AppSettings.MaxAutoBackupHours}시간)를 벗어나 {AppSettings.DefaultAutoBackupHours}시간으로 되돌렸습니다.");
                autoBackup = AppSettings.DefaultAutoBackupHours;
            }
            return (s with { Threshold = threshold, Roi = roi, IdleTimeoutMinutes = idle, AutoBackupHours = autoBackup, FolderPath = s.FolderPath ?? "", ModelPath = s.ModelPath ?? "" },
                warnings.Count == 0 ? null : string.Join(" ", warnings));
        }
        // JSON 문법 오류·읽기 권한 문제 등은 앱 시작을 막지 않고 기본값으로 시작하며 사유를 경고로 알린다.
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return (new AppSettings(), $"설정 파일을 읽을 수 없어 기본값을 사용합니다: {ex.Message}");
        }
    }

    /// <summary>임시 파일에 쓴 뒤 교체한다(쓰는 도중 꺼져도 기존 파일이 깨지지 않게).</summary>
    public void Save(AppSettings s)
    {
        // 저장 전 검증: 잘못된 설정은 파일에 쓰지 않고 예외로 거부한다(불러올 때의 보정과 달리 호출자에게 즉시 알림).
        if (s.Roi?.Validate() is { } problem) throw new ArgumentException(problem, nameof(s));
        if (!double.IsFinite(s.Threshold) || s.Threshold < 0 || s.Threshold > 1)
            throw new ArgumentException("임계값은 0~1 사이여야 합니다.", nameof(s));
        if (!IdleMonitor.IsValidMinutes(s.IdleTimeoutMinutes))
            throw new ArgumentException($"자동 로그아웃 시간은 {IdleMonitor.MinMinutes}~{IdleMonitor.MaxMinutes}분이어야 합니다.", nameof(s));

        if (!AppSettings.IsValidAutoBackupHours(s.AutoBackupHours))
            throw new ArgumentException($"자동 백업 주기는 0~{AppSettings.MaxAutoBackupHours}시간이어야 합니다.", nameof(s));

        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // 안전한 저장: 임시 파일에 먼저 쓰고 덮어쓰기 이동한다. 쓰는 도중 정전·종료가 나도 기존 설정 파일이 반쯤 쓰인 채 남지 않는다.
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json));
        File.Move(tmp, Path, overwrite: true);
    }
}
