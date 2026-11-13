using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisionQC.Core;

namespace VisionQC.App.ViewModels;

/// <summary>목록 한 줄. 임계값이 바뀌면 재판정 열만 갱신한다.</summary>
public partial class ReviewRow : ObservableObject
{
    public ReviewRow(ReviewedResult item) => Item = item;
    public ReviewedResult Item { get; }

    public long Id => Item.Result.Id;
    public string TimeText => Item.Result.InspectedAtUtc.ToLocalTime().ToString("MM-dd HH:mm:ss");
    public string FileName => System.IO.Path.GetFileName(Item.Result.ImagePath);
    public string OriginalText => Item.Result.Verdict.ToString();
    public string ScoreText => Item.Result.Verdict == Verdict.ERROR ? "-" : Item.Result.Score.ToString("F3");
    public string CorrectedText => Item.Latest is { } l ? (Item.IsMisjudged ? $"{l.Corrected} ⚠오판" : $"{l.Corrected} ✓확인") : "-";

    [ObservableProperty] private string _previewText = "";

    public void UpdatePreview(float threshold)
    {
        var now = Rejudge.Apply(Item.Result.Verdict, Item.Result.Score, threshold);
        PreviewText = now == Item.Result.Verdict ? now.ToString() : $"{now} (변경)";
    }
}

public partial class ReviewViewModel : ObservableObject
{
    private readonly IResultRepository _repo;
    private readonly ReviewService _service;
    private readonly Session _session;

    public ReviewViewModel(IResultRepository repo, Session session)
    {
        _repo = repo;
        _session = session;
        _service = new ReviewService(repo);
        System.ComponentModel.PropertyChangedEventManager.AddHandler(ViewOptions.Instance, OnViewOptionsChanged, nameof(ViewOptions.ShowHeatmap));
    }

    private void OnViewOptionsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => RedrawImage();

    private void RedrawImage()
    {
        if (Selected?.Item.Result is { } r) SelectedImage = HeatmapRenderer.Compose(r.ImagePath, r.Verdict == Verdict.NG ? r.Heatmap : null, ViewOptions.Instance.ShowHeatmap);
    }

    public string[] VerdictOptions { get; } = { "전체", "OK", "NG", "ERROR" };
    public string[] ReviewOptions { get; } = { "전체", "정정 없음", "정정됨", "오판만" };

    [ObservableProperty] private string _selectedVerdict = "전체";
    [ObservableProperty] private string _selectedReviewFilter = "전체";
    [ObservableProperty] private DateTime? _fromDate;
    [ObservableProperty] private DateTime? _toDate;
    [ObservableProperty] private double _threshold = 0.5;

    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _previewSummary = "";
    [ObservableProperty] private string _truthSummary = "";

    [NotifyCanExecuteChangedFor(nameof(CorrectToOkCommand))]
    [NotifyCanExecuteChangedFor(nameof(CorrectToNgCommand))]
    [ObservableProperty] private ReviewRow? _selected;

    [ObservableProperty] private ImageSource? _selectedImage;
    [ObservableProperty] private string _selectedDetail = "";
    [ObservableProperty] private string _historyText = "";
    [ObservableProperty] private string _note = "";

    public ObservableCollection<ReviewRow> Items { get; } = new();
    private List<ReviewedResult> _loaded = new();

    partial void OnThresholdChanged(double value) => Recompute();

    partial void OnSelectedChanged(ReviewRow? value) => _ = LoadSelectedAsync(value);

    private async Task LoadSelectedAsync(ReviewRow? row)
    {
        Note = "";
        if (row is null) { SelectedImage = null; SelectedDetail = ""; HistoryText = ""; return; }

        var r = row.Item.Result;
        SelectedImage = HeatmapRenderer.Compose(r.ImagePath, r.Verdict == Verdict.NG ? r.Heatmap : null, ViewOptions.Instance.ShowHeatmap);
        SelectedDetail = $"{r.ImagePath}\n{r.InspectedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · 원 판정 {r.Verdict} · 점수 {(r.Verdict == Verdict.ERROR ? "-" : r.Score.ToString("F3"))}"
            + (SelectedImage is null ? "\n※ 이미지 파일을 열 수 없음(이동·삭제됨?)" : "")
            + (r.ErrorMessage is null ? "" : $"\n{r.ErrorMessage}");
        try
        {
            var history = await _repo.GetReviewsAsync(r.Id);
            if (Selected != row) return;       // 그 사이 다른 줄을 골랐으면 버린다
            HistoryText = history.Count == 0
                ? "정정 이력 없음"
                : string.Join("\n", history.Select(h =>
                    $"{h.ReviewedAtUtc.ToLocalTime():MM-dd HH:mm} {h.Reviewer} → {h.Corrected}" + (h.Note is null ? "" : $" ({h.Note})")));
        }
        catch (Exception ex) { HistoryText = $"이력 조회 실패: {ex.Message}"; }
    }

    [RelayCommand]
    public async Task RefreshAsync() => await ReloadAsync(null);

    private async Task ReloadAsync(long? keepSelectedId)
    {
        try
        {
            DateTime? from = FromDate is { } f ? f.Date.ToUniversalTime() : null;
            DateTime? to = ToDate is { } t ? t.Date.AddDays(1).AddTicks(-1).ToUniversalTime() : null;
            Verdict? v = SelectedVerdict == "전체" ? null : Enum.Parse<Verdict>(SelectedVerdict);

            var rows = await _repo.QueryReviewedAsync(new HistoryFilter(from, to, v, 500));
            _loaded = SelectedReviewFilter switch
            {
                "정정 없음" => rows.Where(r => r.Latest is null).ToList(),
                "정정됨" => rows.Where(r => r.Latest is not null).ToList(),
                "오판만" => rows.Where(r => r.IsMisjudged).ToList(),
                _ => rows.ToList()
            };

            Items.Clear();
            foreach (var r in _loaded) Items.Add(new ReviewRow(r));
            Recompute();
            Selected = keepSelectedId is { } id ? Items.FirstOrDefault(x => x.Id == id) : null;
            Message = rows.Count >= 500 ? "최신 500건만 표시합니다." : $"{_loaded.Count}건";
        }
        catch (Exception ex) { Message = $"조회 실패: {ex.Message}"; }
    }

    /// <summary>임계값을 바꾸면 DB·모델을 건드리지 않고 저장된 점수로 재판정 미리보기와 정정 기반 성능을 다시 계산한다.</summary>
    private void Recompute()
    {
        float thr = (float)Threshold;
        foreach (var row in Items) row.UpdatePreview(thr);

        var p = Rejudge.Preview(_loaded.Select(x => x.Result), thr);
        PreviewSummary = $"임계값 {thr:F2} 적용 시 표시된 {_loaded.Count}건 중 NG→OK {p.ToOk}건, OK→NG {p.ToNg}건 바뀜 (ERROR {p.ErrorSkipped}건 제외)";

        var e = Rejudge.EvaluateAgainstCorrections(_loaded, thr);
        TruthSummary = e.Samples == 0
            ? "정정된 건이 없어 성능을 계산할 수 없습니다. 목록에서 결과를 골라 OK/NG로 정정하세요."
            : $"정정된 {e.Samples}건을 정답으로 본 성능 — 재현율 {Pct(e.Recall)} (놓친 불량 {e.Fn}건), 정밀도 {Pct(e.Precision)} (오검출 {e.Fp}건)";
    }

    private static string Pct(double? v) => v is { } x ? x.ToString("P0") : "-";

    private bool CanCorrect() => Selected is not null;

    [RelayCommand(CanExecute = nameof(CanCorrect))]
    private Task CorrectToOkAsync() => CorrectAsync(Verdict.OK);

    [RelayCommand(CanExecute = nameof(CanCorrect))]
    private Task CorrectToNgAsync() => CorrectAsync(Verdict.NG);

    private async Task CorrectAsync(Verdict corrected)
    {
        if (Selected is not { } row) return;
        if (!_session.Can(Permission.ReviewResults)) { Message = "정정 권한이 없습니다(엔지니어 이상)."; return; }
        try
        {
            var note = Note;
            await _service.CorrectAsync(row.Id, corrected, note, _session.User.LoginId);   // 정정자 = 로그인한 계정
            var audited = await _session.AuditAsync(AuditActions.ReviewCorrect,
                $"{row.FileName}: {row.Item.Result.Verdict}→{corrected}" + (string.IsNullOrWhiteSpace(note) ? "" : $" ({note.Trim()})"));
            await ReloadAsync(row.Id);
            Message = $"{row.FileName}: {corrected}(으)로 정정했습니다." + (audited ? "" : " (※ 감사 로그 기록 실패)");
        }
        catch (Exception ex) { Message = $"정정 실패: {ex.Message}"; }
    }
}
