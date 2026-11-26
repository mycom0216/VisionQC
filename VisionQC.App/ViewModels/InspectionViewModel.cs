// ============================================================================================
// InspectionViewModel.cs — "검사" 탭(MainWindow.xaml의 InspectionTab)의 ViewModel (FR-01, 04, 05, 06, 13).
//
// 하는 일: 이미지 폴더·감시 폴더·카메라/영상 파일에서 이미지를 받아 AI 모델(ONNX)로 판정하고, 결과를 화면에 실시간으로 보여 준다.
//   노출하는 명령: StartCommand(검사 시작), StopCommand(중지), BrowseFolderCommand(폴더 선택).
//   노출하는 상태: 진행 카운트(Total/Ok/Ng/Error), 현재 이미지(히트맵 합성 포함), 판정 글자·색, 최근 결과 목록, 상태 메시지.
//
// MVVM 표기법(CommunityToolkit.Mvvm 소스 생성기):
//   [ObservableProperty] private string _foo;   →  Foo 속성이 자동 생성되고, 값이 바뀌면 화면에 알림(PropertyChanged)이 간다.
//   [RelayCommand] private void Bar();          →  BarCommand(ICommand)가 자동 생성되어 XAML에서 Command="{Binding BarCommand}"로 쓴다.
//   [NotifyCanExecuteChangedFor(...)]            →  이 속성이 바뀌면 지정한 명령의 "실행 가능 여부"(버튼 활성 상태)를 다시 계산한다.
//
// 용어: 판정(Verdict) = OK(정상) / NG(불량) / ERROR(이미지 손상·추론 실패 등으로 판정 불가).
//       불량 점수(defect score) = 모델이 낸 불량일 가능성(0~1). 임계값(threshold) 이상이면 NG.
//       ROI(관심 영역) = 이미지 전체가 아니라 검사할 사각형 영역만 잘라 쓰는 설정(설정 탭, FR-11).
// ============================================================================================
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

/// <summary>
/// 검사 탭의 ViewModel. 입력 소스를 고르고 검사를 시작/중지하며, 파이프라인이 내보내는 결과를 UI 스레드에서 받아 화면 상태로 바꾼다.
/// </summary>
public partial class InspectionViewModel : ObservableObject
{
    private readonly AppServices _svc;
    private readonly Session _session;
    // 검사 취소용 토큰 원본. 중지 버튼이 Cancel()을 부르면 파이프라인이 협조적으로 멈춘다. 검사 중에만 값이 있다.
    private CancellationTokenSource? _cts;

    /// <summary>검사 ViewModel을 만든다. 설정이 바뀌면 설정 요약 문구를 갱신하고, 히트맵 표시 옵션이 바뀌면 현재 이미지를 다시 그린다.</summary>
    public InspectionViewModel(AppServices svc, Session session)
    {
        _svc = svc;
        _session = session;
        _folderPath = svc.Effective.FolderPath;
        // 설정 탭에서 저장하면 이 화면의 "모델·임계값·ROI" 요약 줄도 즉시 바뀌게 한다.
        svc.SettingsChanged += () => OnPropertyChanged(nameof(SettingsSummary));
        if (svc.SettingsWarning is { } w) _statusMessage = $"설정 알림: {w}";
        // 약한 이벤트 구독: 전역 객체(ViewOptions.Instance)가 이 ViewModel을 붙잡아 메모리에 남기지 않게 한다. 핸들러는 람다가 아닌 인스턴스 메서드여야 한다.
        System.ComponentModel.PropertyChangedEventManager.AddHandler(ViewOptions.Instance, OnViewOptionsChanged, nameof(ViewOptions.ShowHeatmap));
    }

    // 화면에 보이는 가장 최근 결과. 히트맵 체크박스를 바꿀 때 같은 결과로 이미지를 다시 그리기 위해 기억한다.
    private InspectionResult? _current;

    /// <summary>"불량 위치 히트맵" 체크박스가 바뀌면 현재 이미지를 다시 합성한다. 히트맵은 NG 결과에만 있다.</summary>
    private void OnViewOptionsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_current is { } c) CurrentImage = HeatmapRenderer.Compose(c.ImagePath, c.Verdict == Verdict.NG ? c.Heatmap : null, ViewOptions.Instance.ShowHeatmap);
    }

    /// <summary>검사할(또는 감시할) 이미지 폴더 경로. 텍스트 상자와 양방향 바인딩된다.</summary>
    [ObservableProperty] private string _folderPath;

    /// <summary>입력 모드 콤보박스의 항목(순서가 ModeIndex와 대응한다).</summary>
    public string[] ModeOptions { get; } = { "폴더 일괄 검사", "폴더 감시(새 이미지 자동 검사)", "카메라 / 영상 파일" };

    /// <summary>0=폴더 일괄, 1=폴더 감시(시작 이후 생긴 이미지를 계속 검사, 중지로 끝), 2=카메라·영상 파일.</summary>
    [NotifyPropertyChangedFor(nameof(IsFolderMode))]
    [NotifyPropertyChangedFor(nameof(IsCameraMode))]
    [NotifyPropertyChangedFor(nameof(WatchMode))]
    [ObservableProperty] private int _modeIndex;

    // 모드에 따라 폴더 입력칸 또는 카메라 입력칸만 보이게 하는 파생 속성(XAML에서 Visibility에 바인딩).
    public bool IsCameraMode => ModeIndex == 2;
    public bool IsFolderMode => ModeIndex != 2;
    public bool WatchMode => ModeIndex == 1;

    /// <summary>카메라 번호("0", "1" …) 또는 영상 파일 경로. 영상은 끝나면 자동 종료, 카메라는 중지할 때까지.</summary>
    // 카메라 번호/영상 경로, 그리고 촬영 간격(초, 문자열로 받아 StartAsync에서 숫자 검사).
    [ObservableProperty] private string _cameraSpec = "0";
    [ObservableProperty] private string _captureIntervalText = "1";

    /// <summary>모델·임계값·ROI는 설정 탭(엔지니어 이상)에서 바꾼다. 여기서는 지금 값을 보여 주기만 한다.</summary>
    // 설정 문구는 읽기 전용 — 값 변경은 설정 탭에서만 가능(엔지니어 이상)하다는 정책을 화면에서도 드러낸다.
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

    /// <summary>검사 진행 중 여부. 바뀌면 시작/중지 버튼의 활성 상태를 다시 계산한다(진행 중에는 시작 불가, 중지만 가능).</summary>
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [ObservableProperty] private bool _isRunning;

    // 이번 실행의 누적 카운트(총 장수, OK, NG, ERROR).
    [ObservableProperty] private int _total;
    [ObservableProperty] private int _ok;
    [ObservableProperty] private int _ng;
    [ObservableProperty] private int _error;
    [ObservableProperty] private string _statusMessage = "폴더를 선택한 뒤 시작하세요.";

    // 큰 화면의 이미지(NG면 히트맵 합성), 판정 글자("OK/NG/ERROR")와 그 배경색, 파일명·점수·처리시간 등 상세 글자.
    [ObservableProperty] private ImageSource? _currentImage;
    [ObservableProperty] private string _verdictText = "-";
    [ObservableProperty] private Brush _verdictBrush = Brushes.Gray;
    [ObservableProperty] private string _detailText = "";

    /// <summary>오른쪽 "최근 결과" 목록. 새 결과를 맨 앞에 넣고 최대 200개까지만 유지한다(메모리 보호).</summary>
    public ObservableCollection<InspectionResult> Recent { get; } = new();

    /// <summary>
    /// 양품률 = OK/(OK+NG). 통계 화면과 같은 규칙(QualityMath).
    /// 양품률에 쓰는 값(Ok, Ng)이 바뀔 때 알린다 — 예전에는 Total이 바뀔 때만 알려서 한 건씩 늦게 갱신되던 버그가 있었다.
    /// </summary>
    public string YieldText => QualityMath.YieldRate(Ok, Ng) is { } y ? y.ToString("P0") : "-";
    // 소스 생성기가 만들어 둔 "속성 변경 후" 훅. Ok/Ng가 바뀔 때마다 양품률 글자를 다시 계산하도록 알린다.
    partial void OnOkChanged(int value) => OnPropertyChanged(nameof(YieldText));
    partial void OnNgChanged(int value) => OnPropertyChanged(nameof(YieldText));

    /// <summary>"찾아보기" 버튼 — 폴더 선택 대화상자를 열어 FolderPath를 채운다.</summary>
    [RelayCommand]
    private void BrowseFolder()
    {
        var dlg = new OpenFolderDialog { Title = "검사할 이미지 폴더" };
        if (dlg.ShowDialog() == true) FolderPath = dlg.FolderName;
    }

    /// <summary>
    /// "시작" 버튼. 흐름: 권한 확인 → 입력 검증(폴더 존재·카메라 입력·촬영 간격) → 모델 로드 → 감사 로그 기록 → 파이프라인 실행 → 결과 요약.
    /// 파이프라인(InspectionPipeline)은 이미지 소스 → 전처리 → ONNX 추론 → 임계값 판정 → DB 저장을 이어서 수행한다.
    /// 검사 중에도 화면이 멈추지 않는 것은 await로 비동기 실행하기 때문이다(NFR-03).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        // 화면 단에서의 권한 확인(RunInspection은 모든 역할에 허용). 입력 오류는 예외 대신 StatusMessage로 알리고 조용히 끝낸다.
        if (!_session.Can(Permission.RunInspection)) { StatusMessage = "검사 권한이 없습니다."; return; }
        bool camera = IsCameraMode, watch = WatchMode;
        TimeSpan interval = default;
        if (camera)
        {
            if (string.IsNullOrWhiteSpace(CameraSpec)) { StatusMessage = "카메라 번호 또는 영상 파일을 입력하세요."; return; }
            if (!double.TryParse(CaptureIntervalText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sec)
                || sec < 0.05 || sec > 3600) { StatusMessage = "촬영 간격은 0.05~3600초의 숫자여야 합니다."; return; }
            interval = TimeSpan.FromSeconds(sec);
        }
        else if (!Directory.Exists(FolderPath)) { StatusMessage = "이미지 폴더를 찾을 수 없습니다."; return; }
        var cfg = _svc.Effective;
        if (string.IsNullOrWhiteSpace(cfg.ModelPath)) { StatusMessage = "모델이 설정되어 있지 않습니다. 엔지니어에게 설정을 요청하세요."; return; }

        // 검사를 시작할 때마다 모델을 새로 연다(설정 탭에서 모델을 바꿔도 다음 검사부터 바로 반영). 열기 실패는 메시지로 알리고 중단한다.
        OnnxClassifier engine;
        try { engine = new OnnxClassifier(cfg.ModelPath); }
        catch (Exception ex) { StatusMessage = $"모델을 열 수 없습니다: {ex.Message}"; return; }

        // 마지막으로 쓴 폴더를 설정 파일에 기억한다(카메라/영상은 해당 없음). 캡처 이미지는 날짜별 하위 폴더에 저장된다.
        if (!camera) _svc.RememberFolder(FolderPath);
        var captureDir = Path.Combine(_svc.CaptureDir, DateTime.Now.ToString("yyyyMMdd"));
        Total = Ok = Ng = Error = 0;
        Recent.Clear();
        VerdictText = "-"; VerdictBrush = Brushes.Gray; DetailText = ""; CurrentImage = null;
        IsRunning = true;
        StatusMessage = camera
            ? $"촬영 중… {CameraSpec.Trim()} 에서 {interval.TotalSeconds:G3}초 간격으로 프레임을 검사합니다(영상 파일은 끝나면 자동 종료, 카메라는 중지 버튼으로 끝냄). (모델 {engine.Model.Name} / {engine.Model.Version})"
            : watch
                ? $"감시 중… 폴더에 새로 생기는 이미지를 자동 검사합니다. 중지 버튼으로 끝냅니다. (모델 {engine.Model.Name} / {engine.Model.Version})"
                : $"검사 중… (모델 {engine.Model.Name} / {engine.Model.Version})";
        _cts = new CancellationTokenSource();

        // 검사에 쓰는 설정(임계값·ROI)을 이 시점의 값으로 고정해 파이프라인에 넘긴다. 검사 도중에 설정을 바꿔도 이번 회차에는 영향이 없다.
        var settings = cfg.ToInspectionSettings();
        await _session.AuditAsync(AuditActions.InspectionStart,
            (camera ? $"카메라/영상 {CameraSpec.Trim()} ({interval.TotalSeconds:G3}초 간격, 캡처 {captureDir})" : FolderPath)
            + $" · 모델 {engine.Model.Version} · 임계값 {cfg.Threshold:F3}" + (cfg.Roi is null ? "" : " · ROI 사용") + (watch ? " · 폴더 감시" : ""));

        // 부품 조립(의존성 주입): 전처리(OpenCV)·추론(ONNX)·판정(임계값)·저장(DB)을 끼워 넣는다. 각 부품은 인터페이스라 교체할 수 있다(NFR-06).
        var pipeline = new InspectionPipeline(new OpenCvPreprocessor(), engine, new ThresholdJudge(), _svc.Results);
        // Progress<T>는 만든 스레드(UI 스레드)의 SynchronizationContext로 콜백을 보낸다. 파이프라인이 백그라운드에서 결과를 내도
        // OnResult는 UI 스레드에서 실행되므로 Dispatcher 호출 없이 바인딩된 속성을 바꿔도 안전하다.
        var progress = new Progress<InspectionResult>(OnResult);   // UI 스레드에서 만들었으므로 콜백도 UI 스레드

        try
        {
            // 입력 모드에 맞는 이미지 소스를 고른다(폴더 일괄 / 폴더 감시 / 카메라·영상). 파이프라인은 어떤 소스인지 몰라도 된다(FR-02).
            IImageSource source = camera ? new VideoCaptureImageSource(CameraSpec, captureDir, interval)
                                : watch ? new FolderWatchImageSource(FolderPath)
                                : new FolderImageSource(FolderPath);
            var sum = await pipeline.RunAsync(source, settings, progress, _cts.Token);
            // 종료 사유별 요약 문구. Cancelled는 사용자가 중지한 정상 종료(카메라·감시는 중지가 곧 정상 종료 방식)이다.
            StatusMessage = sum.Status switch
            {
                JobStatus.Completed => $"완료: 총 {sum.Total}장 (OK {sum.Ok} / NG {sum.Ng} / ERROR {sum.Error})",
                JobStatus.Cancelled when camera => $"촬영 종료: {sum.Total}장 처리 (OK {sum.Ok} / NG {sum.Ng} / ERROR {sum.Error})",
                JobStatus.Cancelled when watch => $"감시 종료: {sum.Total}장 처리 (OK {sum.Ok} / NG {sum.Ng} / ERROR {sum.Error})",
                JobStatus.Cancelled => $"중지됨: {sum.Total}장 처리",
                _ => $"실패: {sum.FatalError}"
            };
            if (sum.SaveFailures > 0) StatusMessage += $"  ※ 이력 저장 실패 {sum.SaveFailures}건";
            await _session.AuditAsync(AuditActions.InspectionEnd,
                $"{sum.Status} · 총 {sum.Total} (OK {sum.Ok} / NG {sum.Ng} / ERROR {sum.Error}) · 회차 {sum.JobId}");
        }
        catch (Exception ex) { StatusMessage = $"오류: {ex.Message}"; }
        // 정상·중지·오류 어느 경우든 반드시 실행된다: 모델(네이티브 리소스)을 해제하고 취소 토큰을 정리한 뒤 IsRunning을 풀어 시작 버튼을 다시 활성화한다.
        finally
        {
            engine.Dispose();
            _cts.Dispose(); _cts = null;
            IsRunning = false;
        }
    }

    /// <summary>시작 버튼의 활성 조건: 이미 실행 중이 아닐 때만.</summary>
    private bool CanStart() => !IsRunning;

    /// <summary>"중지" 버튼. 취소를 요청하면 파이프라인이 현재 이미지를 마친 뒤 멈추고 StartAsync가 요약을 마무리한다.</summary>
    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop() => _cts?.Cancel();

    /// <summary>
    /// 파이프라인이 이미지 한 장을 판정할 때마다 호출된다. 카운트를 올리고, 판정 글자·색(OK 초록 / NG 빨강 / ERROR 주황)과 상세,
    /// 히트맵이 입혀진 이미지(NG만)를 갱신하고, 최근 결과 목록 맨 앞에 추가한다.
    /// </summary>
    private void OnResult(InspectionResult r)
    {
        Total++;
        switch (r.Verdict) { case Verdict.OK: Ok++; break; case Verdict.NG: Ng++; break; default: Error++; break; }

        VerdictText = r.Verdict.ToString();
        VerdictBrush = r.Verdict switch { Verdict.OK => Brushes.SeaGreen, Verdict.NG => Brushes.Crimson, _ => Brushes.DarkOrange };
        DetailText = r.Verdict == Verdict.ERROR
            ? $"{Path.GetFileName(r.ImagePath)}\n{r.ErrorMessage}"
            : $"{Path.GetFileName(r.ImagePath)}\n불량 점수 {r.Score:F3} · {r.ElapsedMs} ms";
        _current = r;
        CurrentImage = HeatmapRenderer.Compose(r.ImagePath, r.Verdict == Verdict.NG ? r.Heatmap : null, ViewOptions.Instance.ShowHeatmap);

        Recent.Insert(0, r);
        if (Recent.Count > 200) Recent.RemoveAt(Recent.Count - 1);
    }
}

/// <summary>이미지 파일을 화면 표시용 BitmapImage로 읽는 도우미(검사·이력·재판정 화면과 HeatmapRenderer가 공용으로 쓴다).</summary>
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
