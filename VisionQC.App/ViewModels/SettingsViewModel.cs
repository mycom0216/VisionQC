using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using VisionQC.Core;

namespace VisionQC.App.ViewModels;

/// <summary>모델·임계값·ROI 편집. 저장하면 파일에 쓰고 감사 로그에 변경 내용을 남긴다(엔지니어 이상).</summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _svc;
    private readonly Session _session;
    private bool _syncing;

    public SettingsViewModel(AppServices svc, Session session)
    {
        _svc = svc;
        _session = session;
        LoadFromEffective();
        FileInfo = $"설정 파일: {svc.SettingsStore.Path}" + (svc.SettingsWarning is { } w ? $"\n알림: {w}" : "")
                   + (svc.Args.ContainsKey("model") || svc.Args.ContainsKey("threshold")
                        ? "\n※ 실행 인자(--model/--threshold)로 시작해 화면 값이 저장된 값과 다를 수 있습니다. 저장하면 이 값이 파일에 기록됩니다." : "");
    }

    [ObservableProperty] private string _modelPath = "";
    [ObservableProperty] private double _threshold = 0.5;
    [ObservableProperty] private string _thresholdText = "0.500";
    [ObservableProperty] private bool _useRoi;
    [ObservableProperty] private double _roiX;
    [ObservableProperty] private double _roiY;
    [ObservableProperty] private double _roiW = 100;
    [ObservableProperty] private double _roiH = 100;
    [ObservableProperty] private string _idleMinutesText = "15";
    [ObservableProperty] private string _autoBackupHoursText = "24";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _fileInfo = "";

    partial void OnThresholdChanged(double value)
    {
        if (_syncing) return;
        _syncing = true;
        ThresholdText = value.ToString("F3", CultureInfo.InvariantCulture);
        _syncing = false;
    }

    partial void OnThresholdTextChanged(string value)
    {
        if (_syncing) return;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 0 && d <= 1)
        {
            _syncing = true;
            Threshold = d;
            _syncing = false;
            Message = "";
        }
        else Message = "임계값은 0~1 사이의 숫자여야 합니다.";
    }

    private void LoadFromEffective()
    {
        var s = _svc.Effective;
        ModelPath = s.ModelPath;
        Threshold = s.Threshold;
        UseRoi = s.Roi is not null;
        RoiX = Math.Round((s.Roi?.X ?? 0) * 100);
        RoiY = Math.Round((s.Roi?.Y ?? 0) * 100);
        RoiW = Math.Round((s.Roi?.W ?? 1) * 100);
        RoiH = Math.Round((s.Roi?.H ?? 1) * 100);
        IdleMinutesText = s.IdleTimeoutMinutes.ToString(CultureInfo.InvariantCulture);
        AutoBackupHoursText = s.AutoBackupHours.ToString(CultureInfo.InvariantCulture);
        Message = "";
    }

    [RelayCommand]
    private void BrowseModel()
    {
        var dlg = new OpenFileDialog { Title = "ONNX 모델 선택", Filter = "ONNX 모델 (*.onnx)|*.onnx" };
        if (dlg.ShowDialog() == true) ModelPath = dlg.FileName;
    }

    [RelayCommand]
    private void Revert() => LoadFromEffective();

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!_session.Can(Permission.ChangeSettings)) { Message = "설정을 바꿀 권한이 없습니다."; return; }
        if (!double.TryParse(ThresholdText, NumberStyles.Float, CultureInfo.InvariantCulture, out var thr) || thr < 0 || thr > 1)
        { Message = "임계값은 0~1 사이의 숫자여야 합니다."; return; }

        Roi? roi = null;
        if (UseRoi)
        {
            roi = new Roi(RoiX / 100, RoiY / 100, RoiW / 100, RoiH / 100);
            if (roi.Validate() is { } problem) { Message = problem; return; }
        }

        if (!int.TryParse(IdleMinutesText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idle) || !IdleMonitor.IsValidMinutes(idle))
        { Message = $"자동 로그아웃 시간은 {IdleMonitor.MinMinutes}~{IdleMonitor.MaxMinutes}분의 정수여야 합니다."; return; }

        if (!int.TryParse(AutoBackupHoursText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var autoBackup) || !AppSettings.IsValidAutoBackupHours(autoBackup))
        { Message = $"자동 백업 주기는 0(끔)~{AppSettings.MaxAutoBackupHours}시간의 정수여야 합니다."; return; }

        var cur = _svc.Effective;
        var model = ModelPath.Trim();
        if (model.Length > 0 && model != cur.ModelPath && !File.Exists(model))
        { Message = "모델 파일을 찾을 수 없습니다."; return; }

        var next = cur with { ModelPath = model, Threshold = thr, Roi = roi, IdleTimeoutMinutes = idle, AutoBackupHours = autoBackup };
        var changes = Describe(cur, next);
        if (changes.Count == 0) { Message = "변경된 내용이 없습니다."; return; }

        try { _svc.ApplySettings(next); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        { Message = $"저장 실패: {ex.Message}"; return; }

        var audited = await _session.AuditAsync(AuditActions.SettingsChange, string.Join(" · ", changes));
        Message = "저장했습니다: " + string.Join(", ", changes) + (audited ? "" : "  (※ 감사 로그 기록 실패)");
    }

    internal static List<string> Describe(AppSettings a, AppSettings b)
    {
        var list = new List<string>();
        if (a.ModelPath != b.ModelPath) list.Add($"모델 {Name(a.ModelPath)}→{Name(b.ModelPath)}");
        if (Math.Abs(a.Threshold - b.Threshold) > 1e-9) list.Add($"임계값 {a.Threshold:F3}→{b.Threshold:F3}");
        if (a.Roi != b.Roi) list.Add($"ROI {RoiText(a.Roi)}→{RoiText(b.Roi)}");
        if (a.IdleTimeoutMinutes != b.IdleTimeoutMinutes) list.Add($"자동 로그아웃 {a.IdleTimeoutMinutes}분→{b.IdleTimeoutMinutes}분");
        if (a.AutoBackupHours != b.AutoBackupHours) list.Add($"자동 백업 {a.AutoBackupHours}시간→{b.AutoBackupHours}시간");
        return list;
    }

    private static string Name(string path) => string.IsNullOrWhiteSpace(path) ? "없음" : Path.GetFileName(path);
    private static string RoiText(Roi? r) => r is null ? "전체" : $"{r.X:P0},{r.Y:P0} {r.W:P0}×{r.H:P0}";
}
