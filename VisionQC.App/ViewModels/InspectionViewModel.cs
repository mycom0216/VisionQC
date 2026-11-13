using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using VisionQC.Core;
using VisionQC.Imaging;
using VisionQC.Inference;

namespace VisionQC.App.ViewModels;

public partial class InspectionViewModel : ObservableObject
{
    private readonly AppServices _svc;
    private readonly Session _session;
    private CancellationTokenSource? _cts;

    public InspectionViewModel(AppServices svc, Session session)
    {
        _svc = svc;
        _session = session;
        _folderPath = svc.Effective.FolderPath;
        svc.SettingsChanged += () => OnPropertyChanged(nameof(SettingsSummary));
        if (svc.SettingsWarning is { } w) _statusMessage = $"설정 알림: {w}";
    }

    [ObservableProperty] private string _folderPath;

    /// <summary>true면 폴더에 새로 생기는 이미지를 계속 검사한다(중지 버튼으로 끝). 시작할 때 이미 있던 파일은 검사하지 않는다.</summary>
    [ObservableProperty] private bool _watchMode;

    /// <summary>모델·임계값·ROI는 설정 탭(엔지니어 이상)에서 바꾼다. 여기서는 지금 값을 보여 주기만 한다.</summary>
    public string SettingsSummary
    {
        get
        {
            var s = _svc.Effective;
            var model = string.IsNullOrWhiteSpace(s.ModelPath) ? "(모델 미설정 — 설정 탭에서 지정)" : Path.GetFileName(s.ModelPath);
            var roi = s.Roi is { } r ? $"ROI {r.X:P0},{r.Y:P0} {r.W:P0}×{r.H:P0}" : "ROI 전체";
            return $"모델 {model} · 임계값 {s.Threshold:F3} · {roi}   (변경: 설정 탭, 엔지니어 이상)";
        }
    }

    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [ObservableProperty] private bool _isRunning;

    [ObservableProperty] private int _total;
    [ObservableProperty] private int _ok;
    [ObservableProperty] private int _ng;
    [ObservableProperty] private int _error;
    [ObservableProperty] private string _statusMessage = "폴더를 선택한 뒤 시작하세요.";

    [ObservableProperty] private ImageSource? _currentImage;
    [ObservableProperty] private string _verdictText = "-";
    [ObservableProperty] private Brush _verdictBrush = Brushes.Gray;
    [ObservableProperty] private string _detailText = "";

    public ObservableCollection<InspectionResult> Recent { get; } = new();

    /// <summary>
    /// 양품률 = OK/(OK+NG). 통계 화면과 같은 규칙(QualityMath).
    /// 양품률에 쓰는 값(Ok, Ng)이 바뀔 때 알린다 — 예전에는 Total이 바뀔 때만 알려서 한 건씩 늦게 갱신되던 버그가 있었다.
    /// </summary>
    public string YieldText => QualityMath.YieldRate(Ok, Ng) is { } y ? y.ToString("P0") : "-";
    partial void OnOkChanged(int value) => OnPropertyChanged(nameof(YieldText));
    partial void OnNgChanged(int value) => OnPropertyChanged(nameof(YieldText));

    [RelayCommand]
    private void BrowseFolder()
    {
        var dlg = new OpenFolderDialog { Title = "검사할 이미지 폴더" };
        if (dlg.ShowDialog() == true) FolderPath = dlg.FolderName;
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (!_session.Can(Permission.RunInspection)) { StatusMessage = "검사 권한이 없습니다."; return; }
        if (!Directory.Exists(FolderPath)) { StatusMessage = "이미지 폴더를 찾을 수 없습니다."; return; }
        var cfg = _svc.Effective;
        if (string.IsNullOrWhiteSpace(cfg.ModelPath)) { StatusMessage = "모델이 설정되어 있지 않습니다. 엔지니어에게 설정을 요청하세요."; return; }

        OnnxClassifier engine;
        try { engine = new OnnxClassifier(cfg.ModelPath); }
        catch (Exception ex) { StatusMessage = $"모델을 열 수 없습니다: {ex.Message}"; return; }

        _svc.RememberFolder(FolderPath);
        Total = Ok = Ng = Error = 0;
        Recent.Clear();
        VerdictText = "-"; VerdictBrush = Brushes.Gray; DetailText = ""; CurrentImage = null;
        IsRunning = true;
        bool watch = WatchMode;
        StatusMessage = watch
            ? $"감시 중… 폴더에 새로 생기는 이미지를 자동 검사합니다. 중지 버튼으로 끝냅니다. (모델 {engine.Model.Name} / {engine.Model.Version})"
            : $"검사 중… (모델 {engine.Model.Name} / {engine.Model.Version})";
        _cts = new CancellationTokenSource();

        var settings = cfg.ToInspectionSettings();
        await _session.AuditAsync(AuditActions.InspectionStart,
            $"{FolderPath} · 모델 {engine.Model.Version} · 임계값 {cfg.Threshold:F3}" + (cfg.Roi is null ? "" : " · ROI 사용") + (watch ? " · 폴더 감시" : ""));

        var pipeline = new InspectionPipeline(new OpenCvPreprocessor(), engine, new ThresholdJudge(), _svc.Results);
        var progress = new Progress<InspectionResult>(OnResult);   // UI 스레드에서 만들었으므로 콜백도 UI 스레드

        try
        {
            IImageSource source = watch ? new FolderWatchImageSource(FolderPath) : new FolderImageSource(FolderPath);
            var sum = await pipeline.RunAsync(source, settings, progress, _cts.Token);
            StatusMessage = sum.Status switch
            {
                JobStatus.Completed => $"완료: 총 {sum.Total}장 (OK {sum.Ok} / NG {sum.Ng} / ERROR {sum.Error})",
                JobStatus.Cancelled when watch => $"감시 종료: {sum.Total}장 처리 (OK {sum.Ok} / NG {sum.Ng} / ERROR {sum.Error})",
                JobStatus.Cancelled => $"중지됨: {sum.Total}장 처리",
                _ => $"실패: {sum.FatalError}"
            };
            if (sum.SaveFailures > 0) StatusMessage += $"  ※ 이력 저장 실패 {sum.SaveFailures}건";
            await _session.AuditAsync(AuditActions.InspectionEnd,
                $"{sum.Status} · 총 {sum.Total} (OK {sum.Ok} / NG {sum.Ng} / ERROR {sum.Error}) · 회차 {sum.JobId}");
        }
        catch (Exception ex) { StatusMessage = $"오류: {ex.Message}"; }
        finally
        {
            engine.Dispose();
            _cts.Dispose(); _cts = null;
            IsRunning = false;
        }
    }

    private bool CanStart() => !IsRunning;

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop() => _cts?.Cancel();

    private void OnResult(InspectionResult r)
    {
        Total++;
        switch (r.Verdict) { case Verdict.OK: Ok++; break; case Verdict.NG: Ng++; break; default: Error++; break; }

        VerdictText = r.Verdict.ToString();
        VerdictBrush = r.Verdict switch { Verdict.OK => Brushes.SeaGreen, Verdict.NG => Brushes.Crimson, _ => Brushes.DarkOrange };
        DetailText = r.Verdict == Verdict.ERROR
            ? $"{Path.GetFileName(r.ImagePath)}\n{r.ErrorMessage}"
            : $"{Path.GetFileName(r.ImagePath)}\n불량 점수 {r.Score:F3} · {r.ElapsedMs} ms";
        CurrentImage = ImageLoader.Load(r.ImagePath);

        Recent.Insert(0, r);
        if (Recent.Count > 200) Recent.RemoveAt(Recent.Count - 1);
    }
}

internal static class ImageLoader
{
    /// <summary>파일 잠금 없이(OnLoad 캐시) 읽고, 큰 이미지는 표시용으로 줄여서 연다. 실패하면 null.</summary>
    public static ImageSource? Load(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 1200;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }
}
