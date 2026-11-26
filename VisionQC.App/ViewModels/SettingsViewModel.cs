// ============================================================================================
// SettingsViewModel.cs — "설정" 탭(SettingsTab)의 ViewModel (FR-11, 엔지니어 이상 변경 가능).
//
// 하는 일: 모델 파일(.onnx), 임계값(threshold), ROI, 자동 로그아웃 시간, 자동 백업 주기를 편집하고 저장한다.
//   노출하는 명령: BrowseModelCommand(모델 파일 선택), SaveCommand(검증 후 저장), RevertCommand(되돌리기).
//   저장 규칙: 값 검증 → 변경 내용 없으면 중단 → 설정 파일에 저장(AppServices.ApplySettings) → 변경 내용을 감사 로그에 기록(FR-12).
//
// 용어: 임계값 = 불량 점수가 이 값 이상이면 NG로 판정하는 기준(0~1). 낮추면 불량을 더 잘 잡지만 오검출이 늘어난다.
//       ROI(관심 영역) = 이미지 전체 대신 검사할 사각형(X, Y, 폭 W, 높이 H). 여기서는 0~100% 슬라이더이고 저장은 0~1 비율이다.
// ============================================================================================
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
    // 슬라이더(Threshold)와 텍스트 상자(ThresholdText)가 서로를 갱신할 때 무한 반복되지 않게 하는 재진입 방지 플래그.
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

    // 화면 입력값들. 텍스트로 받는 항목(...Text)은 저장할 때 숫자로 검사해 잘못된 입력을 막는다.
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

    // 슬라이더 → 텍스트 상자: 소수 3자리 글자로 맞춘다. _syncing으로 반대 방향 갱신이 되돌아오는 것을 막는다.
    partial void OnThresholdChanged(double value)
    {
        if (_syncing) return;
        _syncing = true;
        ThresholdText = value.ToString("F3", CultureInfo.InvariantCulture);
        _syncing = false;
    }

    // 텍스트 상자 → 슬라이더: 0~1 숫자일 때만 반영하고, 아니면 입력 오류 메시지를 보인다(저장 단계에서 다시 검사).
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

    /// <summary>현재 "지금 쓰는 설정"(Effective)을 화면 입력값으로 불러온다. ROI는 저장 단위(0~1)를 슬라이더 단위(%)로 바꾼다. "되돌리기"도 이것을 쓴다.</summary>
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

    /// <summary>"찾아보기" 버튼 — ONNX 모델 파일을 고른다.</summary>
    [RelayCommand]
    private void BrowseModel()
    {
        var dlg = new OpenFileDialog { Title = "ONNX 모델 선택", Filter = "ONNX 모델 (*.onnx)|*.onnx" };
        if (dlg.ShowDialog() == true) ModelPath = dlg.FileName;
    }

    /// <summary>"되돌리기" 버튼 — 저장하지 않은 편집을 버리고 현재 설정으로 되돌린다.</summary>
    [RelayCommand]
    private void Revert() => LoadFromEffective();

    /// <summary>
    /// "저장" 버튼. 권한 확인 → 임계값·ROI·자동 로그아웃·자동 백업 값 검증 → 모델 파일 존재 확인 → 변경 내용 요약 → 저장 → 감사 로그 기록.
    /// 검증 실패는 예외 대신 Message에 사유를 보이고 저장하지 않는다.
    /// </summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!_session.Can(Permission.ChangeSettings)) { Message = "설정을 바꿀 권한이 없습니다."; return; }
        if (!double.TryParse(ThresholdText, NumberStyles.Float, CultureInfo.InvariantCulture, out var thr) || thr < 0 || thr > 1)
        { Message = "임계값은 0~1 사이의 숫자여야 합니다."; return; }

        Roi? roi = null;
        if (UseRoi)
        {
            // 화면의 %(0~100)를 저장 단위 비율(0~1)로 바꾼다. Validate()는 영역이 이미지를 벗어나거나 너무 작은 경우의 사유를 돌려준다.
            roi = new Roi(RoiX / 100, RoiY / 100, RoiW / 100, RoiH / 100);
            if (roi.Validate() is { } problem) { Message = problem; return; }
        }

        if (!int.TryParse(IdleMinutesText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idle) || !IdleMonitor.IsValidMinutes(idle))
        { Message = $"자동 로그아웃 시간은 {IdleMonitor.MinMinutes}~{IdleMonitor.MaxMinutes}분의 정수여야 합니다."; return; }

        if (!int.TryParse(AutoBackupHoursText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var autoBackup) || !AppSettings.IsValidAutoBackupHours(autoBackup))
        { Message = $"자동 백업 주기는 0(끔)~{AppSettings.MaxAutoBackupHours}시간의 정수여야 합니다."; return; }

        var cur = _svc.Effective;
        var model = ModelPath.Trim();
        // 모델은 "경로가 바뀌었을 때만" 파일 존재를 검사한다(이미 지정돼 있던 경로는 그대로 두고 다른 항목만 고칠 수 있도록).
        if (model.Length > 0 && model != cur.ModelPath && !File.Exists(model))
        { Message = "모델 파일을 찾을 수 없습니다."; return; }

        // record의 with 식: 현재 설정을 복사해 바뀐 항목만 갈아 끼운 새 설정을 만든다(원본은 그대로).
        var next = cur with { ModelPath = model, Threshold = thr, Roi = roi, IdleTimeoutMinutes = idle, AutoBackupHours = autoBackup };
        var changes = Describe(cur, next);
        if (changes.Count == 0) { Message = "변경된 내용이 없습니다."; return; }

        try { _svc.ApplySettings(next); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        { Message = $"저장 실패: {ex.Message}"; return; }

        // 누가 무엇을 바꿨는지(예: 임계값 0.500→0.600) 감사 로그에 남긴다. 기록 실패는 메시지에 경고로만 덧붙인다.
        var audited = await _session.AuditAsync(AuditActions.SettingsChange, string.Join(" · ", changes));
        Message = "저장했습니다: " + string.Join(", ", changes) + (audited ? "" : "  (※ 감사 로그 기록 실패)");
    }

    /// <summary>이전·이후 설정을 비교해 바뀐 항목만 "모델 a→b", "임계값 0.500→0.600" 같은 문장 목록으로 만든다(저장 메시지·감사 로그용).</summary>
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
