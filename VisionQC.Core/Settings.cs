using System.Text.Json;

namespace VisionQC.Core;

/// <summary>관심 영역(ROI). 이미지 크기에 대한 비율(0~1)이라 해상도가 달라도 같은 영역을 가리킨다.</summary>
public sealed record Roi(double X, double Y, double W, double H)
{
    public const double MinSize = 0.05;   // 폭·높이 최소 5%

    /// <summary>문제가 있으면 사유, 없으면 null.</summary>
    public string? Validate()
    {
        if (!double.IsFinite(X) || !double.IsFinite(Y) || !double.IsFinite(W) || !double.IsFinite(H)) return "ROI 값이 유효하지 않습니다.";
        if (X < 0 || Y < 0) return "ROI의 시작 위치는 0 이상이어야 합니다.";
        if (W < MinSize || H < MinSize) return $"ROI의 폭·높이는 {MinSize:P0} 이상이어야 합니다.";
        if (X + W > 1.0 + 1e-9 || Y + H > 1.0 + 1e-9) return "ROI가 이미지 범위를 벗어납니다.";
        return null;
    }

    /// <summary>비율 ROI를 픽셀 사각형으로. 항상 이미지 안에 있고 폭·높이는 최소 1픽셀.</summary>
    public (int X, int Y, int W, int H) ToPixels(int imageWidth, int imageHeight)
    {
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

    public const int DefaultAutoBackupHours = 24, MaxAutoBackupHours = 720;
    public static bool IsValidAutoBackupHours(int h) => h >= 0 && h <= MaxAutoBackupHours;

    public InspectionSettings ToInspectionSettings() => new() { Threshold = (float)Threshold, Roi = Roi };
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping   // 한글 경로를 \uXXXX로 바꾸지 않음
    };

    public string Path { get; }
    public SettingsStore(string path) => Path = path;

    /// <summary>파일이 없으면 기본값. 깨졌거나 값이 잘못됐으면 기본값/보정값으로 시작하고 사유를 돌려준다(앱이 죽지 않는다).</summary>
    public (AppSettings Settings, string? Warning) Load()
    {
        if (!File.Exists(Path)) return (new AppSettings(), null);
        try
        {
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), Json);
            if (s is null) return (new AppSettings(), "설정 파일이 비어 있어 기본값을 사용합니다.");

            var warnings = new List<string>();
            var threshold = s.Threshold;
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
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return (new AppSettings(), $"설정 파일을 읽을 수 없어 기본값을 사용합니다: {ex.Message}");
        }
    }

    /// <summary>임시 파일에 쓴 뒤 교체한다(쓰는 도중 꺼져도 기존 파일이 깨지지 않게).</summary>
    public void Save(AppSettings s)
    {
        if (s.Roi?.Validate() is { } problem) throw new ArgumentException(problem, nameof(s));
        if (!double.IsFinite(s.Threshold) || s.Threshold < 0 || s.Threshold > 1)
            throw new ArgumentException("임계값은 0~1 사이여야 합니다.", nameof(s));
        if (!IdleMonitor.IsValidMinutes(s.IdleTimeoutMinutes))
            throw new ArgumentException($"자동 로그아웃 시간은 {IdleMonitor.MinMinutes}~{IdleMonitor.MaxMinutes}분이어야 합니다.", nameof(s));

        if (!AppSettings.IsValidAutoBackupHours(s.AutoBackupHours))
            throw new ArgumentException($"자동 백업 주기는 0~{AppSettings.MaxAutoBackupHours}시간이어야 합니다.", nameof(s));

        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json));
        File.Move(tmp, Path, overwrite: true);
    }
}
