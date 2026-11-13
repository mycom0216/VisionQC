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
    private readonly IResultRepository _repo;
    private CancellationTokenSource? _cts;

    public InspectionViewModel(IResultRepository repo) => _repo = repo;

    [ObservableProperty] private string _folderPath = "";
    [ObservableProperty] private string _modelPath = "";
    [ObservableProperty] private double _threshold = 0.5;

    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [ObservableProperty] private bool _isRunning;

    [ObservableProperty] private int _total;
    [ObservableProperty] private int _ok;
    [ObservableProperty] private int _ng;
    [ObservableProperty] private int _error;
    [ObservableProperty] private string _statusMessage = "폴더와 모델(.onnx)을 선택한 뒤 시작하세요.";

    [ObservableProperty] private ImageSource? _currentImage;
    [ObservableProperty] private string _verdictText = "-";
    [ObservableProperty] private Brush _verdictBrush = Brushes.Gray;
    [ObservableProperty] private string _detailText = "";

    public ObservableCollection<InspectionResult> Recent { get; } = new();

    public string YieldText => Total == 0 ? "-" : $"{(double)Ok / Total:P0}";
    partial void OnTotalChanged(int value) => OnPropertyChanged(nameof(YieldText));

    [RelayCommand]
    private void BrowseFolder()
    {
        var dlg = new OpenFolderDialog { Title = "검사할 이미지 폴더" };
        if (dlg.ShowDialog() == true) FolderPath = dlg.FolderName;
    }

    [RelayCommand]
    private void BrowseModel()
    {
        var dlg = new OpenFileDialog { Title = "ONNX 모델 선택", Filter = "ONNX 모델 (*.onnx)|*.onnx" };
        if (dlg.ShowDialog() == true) ModelPath = dlg.FileName;
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (!Directory.Exists(FolderPath)) { StatusMessage = "이미지 폴더를 찾을 수 없습니다."; return; }

        OnnxClassifier engine;
        try { engine = new OnnxClassifier(ModelPath); }
        catch (Exception ex) { StatusMessage = $"모델을 열 수 없습니다: {ex.Message}"; return; }

        Total = Ok = Ng = Error = 0;
        Recent.Clear();
        VerdictText = "-"; VerdictBrush = Brushes.Gray; DetailText = ""; CurrentImage = null;
        IsRunning = true;
        StatusMessage = $"검사 중… (모델 {engine.Model.Name} / {engine.Model.Version})";
        _cts = new CancellationTokenSource();

        var settings = new InspectionSettings { Threshold = (float)Threshold };
        var pipeline = new InspectionPipeline(new OpenCvPreprocessor(), engine, new ThresholdJudge(), _repo);
        var progress = new Progress<InspectionResult>(OnResult);   // UI 스레드에서 만들었으므로 콜백도 UI 스레드

        try
        {
            var sum = await pipeline.RunAsync(new FolderImageSource(FolderPath), settings, progress, _cts.Token);
            StatusMessage = sum.Status switch
            {
                JobStatus.Completed => $"완료: 총 {sum.Total}장 (OK {sum.Ok} / NG {sum.Ng} / ERROR {sum.Error})",
                JobStatus.Cancelled => $"중지됨: {sum.Total}장 처리",
                _ => $"실패: {sum.FatalError}"
            };
            if (sum.SaveFailures > 0) StatusMessage += $"  ※ 이력 저장 실패 {sum.SaveFailures}건";
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
