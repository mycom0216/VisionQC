// ============================================================================================
// ReviewViewModel.cs — "재판정" 탭(ReviewTab)의 ViewModel (FR-09, 엔지니어 이상).
//
// 하는 일: 저장된 검사 결과를 조회해 (1) 임계값을 바꿔 보면 판정이 어떻게 달라지는지 미리보기하고(재판정, re-judgement),
//   (2) 사람이 이미지를 보고 OK/NG로 정정(라벨 정정)하며, (3) 정정을 정답으로 삼아 모델의 재현율·정밀도를 계산해 보여 준다.
//   노출하는 명령: RefreshCommand(조회), CorrectToOkCommand / CorrectToNgCommand(정정).
//
// 용어: 재판정(re-judgement) = 저장된 "불량 점수"에 다른 임계값을 적용해 판정을 다시 계산하는 것. 모델·DB는 건드리지 않는다.
//       정정 = 사람이 원 판정을 덮어쓰지 않고 "정정 기록"으로 쌓는 것. 가장 나중 정정이 유효. 원 판정은 그대로 보존된다.
//       오판(misjudged) = 원 판정과 정정 판정이 다른 경우. 재현율 = 실제 불량 중 찾아낸 비율, 정밀도 = NG라고 한 것 중 진짜 불량 비율.
// ============================================================================================
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
    /// <summary>원 검사 결과와 정정 이력을 함께 담은 데이터.</summary>
    public ReviewedResult Item { get; }

    public long Id => Item.Result.Id;
    public string TimeText => Item.Result.InspectedAtUtc.ToLocalTime().ToString("MM-dd HH:mm:ss");
    public string FileName => System.IO.Path.GetFileName(Item.Result.ImagePath);
    public string OriginalText => Item.Result.Verdict.ToString();
    public string ScoreText => Item.Result.Verdict == Verdict.ERROR ? "-" : Item.Result.Score.ToString("F3");
    // 정정이 있으면 가장 최근 정정을 표시: 원 판정과 다르면 "⚠오판", 같으면 "✓확인". 없으면 "-".
    public string CorrectedText => Item.Latest is { } l ? (Item.IsMisjudged ? $"{l.Corrected} ⚠오판" : $"{l.Corrected} ✓확인") : "-";

    /// <summary>현재 슬라이더 임계값을 적용했을 때의 재판정 결과 글자. 원 판정과 다르면 "(변경)"을 붙인다. 이 열만 임계값 변경 때 갱신된다.</summary>
    [ObservableProperty] private string _previewText = "";

    /// <summary>저장된 점수에 새 임계값을 적용해 PreviewText를 갱신한다(Rejudge.Apply — ERROR는 그대로 유지).</summary>
    public void UpdatePreview(float threshold)
    {
        var now = Rejudge.Apply(Item.Result.Verdict, Item.Result.Score, threshold);
        PreviewText = now == Item.Result.Verdict ? now.ToString() : $"{now} (변경)";
    }
}

/// <summary>재판정 탭의 ViewModel. 조회 필터, 임계값 미리보기, 선택 항목 상세, 정정 명령을 관리한다.</summary>
public partial class ReviewViewModel : ObservableObject
{
    // _repo: 결과 저장소(조회), _service: 정정 기록 규칙을 아는 서비스, _session: 정정자·권한 확인용.
    private readonly IResultRepository _repo;
    private readonly ReviewService _service;
    private readonly Session _session;

    /// <summary>저장소와 로그인 세션을 받아 만든다. 히트맵 표시 옵션이 바뀌면 선택한 이미지를 다시 그리도록 구독한다(약한 참조).</summary>
    public ReviewViewModel(IResultRepository repo, Session session)
    {
        _repo = repo;
        _session = session;
        _service = new ReviewService(repo);
        System.ComponentModel.PropertyChangedEventManager.AddHandler(ViewOptions.Instance, OnViewOptionsChanged, nameof(ViewOptions.ShowHeatmap));
    }

    private void OnViewOptionsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => RedrawImage();

    /// <summary>선택한 결과의 이미지를 현재 히트맵 옵션으로 다시 합성한다(NG만 히트맵이 있음).</summary>
    private void RedrawImage()
    {
        if (Selected?.Item.Result is { } r) SelectedImage = HeatmapRenderer.Compose(r.ImagePath, r.Verdict == Verdict.NG ? r.Heatmap : null, ViewOptions.Instance.ShowHeatmap);
    }

    // 필터 콤보박스 항목. "전체"는 필터 없음. ReviewOptions는 정정 여부 필터(정정 없음/정정됨/오판만).
    public string[] VerdictOptions { get; } = { "전체", "OK", "NG", "ERROR" };
    public string[] ReviewOptions { get; } = { "전체", "정정 없음", "정정됨", "오판만" };

    // 조회 조건(판정, 정정 여부, 기간). 날짜는 사용자 로컬 기준으로 입력받는다.
    [ObservableProperty] private string _selectedVerdict = "전체";
    [ObservableProperty] private string _selectedReviewFilter = "전체";
    [ObservableProperty] private DateTime? _fromDate;
    [ObservableProperty] private DateTime? _toDate;
    /// <summary>재판정 미리보기용 임계값(슬라이더, 0~1). 바뀌면 OnThresholdChanged가 목록·요약을 다시 계산한다. 실제 설정(설정 탭)은 바뀌지 않는다.</summary>
    [ObservableProperty] private double _threshold = 0.5;

    // 상태 줄, 임계값 적용 결과 요약(몇 건이 바뀌는지), 정정 기반 성능 요약.
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _previewSummary = "";
    [ObservableProperty] private string _truthSummary = "";

    /// <summary>목록에서 선택한 행. 선택 여부에 따라 정정 버튼의 활성 상태가 바뀐다.</summary>
    [NotifyCanExecuteChangedFor(nameof(CorrectToOkCommand))]
    [NotifyCanExecuteChangedFor(nameof(CorrectToNgCommand))]
    [ObservableProperty] private ReviewRow? _selected;

    // 선택한 결과의 상세: 이미지(히트맵 포함), 설명 글자, 정정 이력 글자, 정정할 때 남길 메모(최대 500자).
    [ObservableProperty] private ImageSource? _selectedImage;
    [ObservableProperty] private string _selectedDetail = "";
    [ObservableProperty] private string _historyText = "";
    [ObservableProperty] private string _note = "";

    /// <summary>DataGrid에 바인딩되는 목록. _loaded는 같은 데이터의 원본 목록으로 성능 계산에 쓴다.</summary>
    public ObservableCollection<ReviewRow> Items { get; } = new();
    private List<ReviewedResult> _loaded = new();

    // 슬라이더를 움직이면 DB 조회 없이 메모리의 점수로 즉시 다시 계산한다(빠른 피드백).
    partial void OnThresholdChanged(double value) => Recompute();

    // 선택이 바뀌면 상세를 불러온다. 정정 이력 조회는 비동기이므로 "fire-and-forget"(_ =)로 시작한다. 예외는 LoadSelectedAsync 안에서 처리한다.
    partial void OnSelectedChanged(ReviewRow? value) => _ = LoadSelectedAsync(value);

    /// <summary>선택한 결과의 이미지·상세·정정 이력을 채운다. 이력 조회 중 다른 행을 선택하면 오래된 결과는 버린다(경쟁 상태 방지).</summary>
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

    /// <summary>"조회" 버튼. 현재 필터로 목록을 다시 읽는다(탭을 열 때도 호출됨).</summary>
    [RelayCommand]
    public async Task RefreshAsync() => await ReloadAsync(null);

    /// <summary>
    /// DB에서 조건에 맞는 결과(최신 500건)를 읽어 목록을 채운다.
    /// </summary>
    /// <param name="keepSelectedId">정정 직후 같은 행을 다시 선택해 두기 위한 결과 ID(없으면 선택 해제).</param>
    private async Task ReloadAsync(long? keepSelectedId)
    {
        try
        {
            // 화면에서 입력한 날짜(로컬 시간대)를 DB 조회용 UTC 구간으로 바꾼다. 종료일은 그날 끝까지 포함한다.
            var (from, to) = HistoryPeriod.ToUtc(FromDate, ToDate, TimeZoneInfo.Local);
            Verdict? v = SelectedVerdict == "전체" ? null : Enum.Parse<Verdict>(SelectedVerdict);

            var rows = await _repo.QueryReviewedAsync(new HistoryFilter(from, to, v, 500));
            // 정정 여부 필터는 DB가 아닌 메모리에서 적용한다(조회한 최대 500건 안에서).
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

        // 정정된 건만 "정답"으로 보고 현재 임계값의 재현율·정밀도를 계산한다. 정정이 없으면 계산할 수 없어 안내 문구를 보인다.
        var e = Rejudge.EvaluateAgainstCorrections(_loaded, thr);
        TruthSummary = e.Samples == 0
            ? "정정된 건이 없어 성능을 계산할 수 없습니다. 목록에서 결과를 골라 OK/NG로 정정하세요."
            : $"정정된 {e.Samples}건을 정답으로 본 성능 — 재현율 {Pct(e.Recall)} (놓친 불량 {e.Fn}건), 정밀도 {Pct(e.Precision)} (오검출 {e.Fp}건)";
    }

    /// <summary>비율을 퍼센트 글자로(값이 없으면 "-").</summary>
    private static string Pct(double? v) => v is { } x ? x.ToString("P0") : "-";

    /// <summary>정정 버튼은 행을 선택했을 때만 활성.</summary>
    private bool CanCorrect() => Selected is not null;

    /// <summary>"정상(OK)으로 정정" 버튼.</summary>
    [RelayCommand(CanExecute = nameof(CanCorrect))]
    private Task CorrectToOkAsync() => CorrectAsync(Verdict.OK);

    /// <summary>"불량(NG)으로 정정" 버튼.</summary>
    [RelayCommand(CanExecute = nameof(CanCorrect))]
    private Task CorrectToNgAsync() => CorrectAsync(Verdict.NG);

    /// <summary>
    /// 선택한 결과를 corrected 판정으로 정정한다. 정정은 원 판정을 덮어쓰지 않고 이력으로 추가된다.
    /// 순서: 권한 확인(엔지니어 이상) → 정정 저장(정정자=로그인 계정) → 감사 로그 기록 → 목록 다시 읽기(같은 행 유지).
    /// </summary>
    private async Task CorrectAsync(Verdict corrected)
    {
        if (Selected is not { } row) return;
        if (!_session.Can(Permission.ReviewResults)) { Message = "정정 권한이 없습니다(엔지니어 이상)."; return; }
        try
        {
            var note = Note;
            await _service.CorrectAsync(row.Id, corrected, note, _session.User.LoginId);   // 정정자 = 로그인한 계정
            // 감사 로그 기록이 실패해도 정정 자체는 이미 저장됐으므로, 사용자에게는 성공 메시지에 경고만 덧붙인다.
            var audited = await _session.AuditAsync(AuditActions.ReviewCorrect,
                $"{row.FileName}: {row.Item.Result.Verdict}→{corrected}" + (string.IsNullOrWhiteSpace(note) ? "" : $" ({note.Trim()})"));
            await ReloadAsync(row.Id);
            Message = $"{row.FileName}: {corrected}(으)로 정정했습니다." + (audited ? "" : " (※ 감사 로그 기록 실패)");
        }
        catch (Exception ex) { Message = $"정정 실패: {ex.Message}"; }
    }
}
