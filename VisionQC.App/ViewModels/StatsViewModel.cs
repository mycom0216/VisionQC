// ============================================================================================
// StatsViewModel.cs — "통계" 탭(StatsTab)의 ViewModel (FR-10).
//
// 하는 일: 기간 안의 검사 결과를 집계해 카드(총·OK·NG·ERROR·양품률), 일별 양품률, 불량 유형 분포, 처리시간 분포를 보여 준다.
//   노출하는 명령: RefreshCommand(조회). 집계 계산은 Core의 StatsCalculator가 하고, 이 클래스는 화면용 글자·막대로 바꿔 담기만 한다.
//   차트 라이브러리 없이 막대 하나를 BarItem(이름·값 글자·0~1 비율)으로 표현하고, XAML의 ProgressBar가 비율만큼 채워 그린다.
//
// 용어: 양품률(yield) = OK ÷ (OK + NG). ERROR는 판정 불가이므로 분모에서 뺀다.
// ============================================================================================
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisionQC.Core;

namespace VisionQC.App.ViewModels;

/// <summary>막대 한 줄. Fraction은 0~1(ProgressBar로 그린다).</summary>
public sealed record BarItem(string Label, string ValueText, double Fraction);

/// <summary>통계 탭의 ViewModel. 기간을 입력받아 DB에서 집계 원자료를 읽고 화면용 속성으로 채운다.</summary>
public partial class StatsViewModel : ObservableObject
{
    private readonly IResultRepository _repo;
    public StatsViewModel(IResultRepository repo) => _repo = repo;

    // 조회 기간(사용자 로컬 날짜)과 상태 메시지.
    [ObservableProperty] private DateTime? _fromDate;
    [ObservableProperty] private DateTime? _toDate;
    [ObservableProperty] private string _message = "";

    // 카드에 표시할 글자들. 데이터가 없으면 "-".
    [ObservableProperty] private string _totalText = "-";
    [ObservableProperty] private string _okText = "-";
    [ObservableProperty] private string _ngText = "-";
    [ObservableProperty] private string _errorText = "-";
    [ObservableProperty] private string _yieldText = "-";
    [ObservableProperty] private string _timeText = "처리시간 데이터 없음";

    /// <summary>막대 목록 3종: 일별 양품률 / 불량 유형별 건수 / 처리시간 구간별 건수. 비우고 다시 채우는 방식으로 갱신한다.</summary>
    public ObservableCollection<BarItem> Daily { get; } = new();
    public ObservableCollection<BarItem> DefectTypes { get; } = new();
    public ObservableCollection<BarItem> TimeHistogram { get; } = new();

    /// <summary>"조회" 버튼(탭을 열 때도 호출). DB에서 집계 원자료를 읽어 StatsCalculator로 계산한 뒤 Apply로 화면에 반영한다.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            // 입력한 날짜는 로컬 기준. To는 그날 끝까지 포함.
            // 입력한 날짜는 로컬 기준 → DB 조회용 UTC 구간으로 변환(종료일은 그날 끝까지 포함).
            var (from, to) = HistoryPeriod.ToUtc(FromDate, ToDate, TimeZoneInfo.Local);

            var data = await _repo.QueryStatsAsync(new StatsFilter(from, to));
            Apply(StatsCalculator.Build(data, TimeZoneInfo.Local));
        }
        catch (Exception ex) { Message = $"통계 조회 실패: {ex.Message}"; }
    }

    /// <summary>계산된 요약(StatsSummary)을 카드 글자와 막대 목록으로 옮긴다.</summary>
    private void Apply(StatsSummary s)
    {
        TotalText = s.Total.ToString("N0");
        OkText = s.Ok.ToString("N0");
        NgText = s.Ng.ToString("N0");
        ErrorText = s.Error.ToString("N0");
        YieldText = s.YieldRate is { } y ? y.ToString("P1") : "-";

        Daily.Clear();
        foreach (var d in s.Daily)
            Daily.Add(new BarItem(d.Date.ToString("MM-dd"),
                d.YieldRate is { } r ? $"{r:P0}  (OK {d.Ok} / NG {d.Ng}" + (d.Error > 0 ? $" / ERROR {d.Error})" : ")") : $"판정 없음 (ERROR {d.Error})",
                d.YieldRate ?? 0));

        DefectTypes.Clear();
        // 분포 막대는 가장 큰 값을 100%로 하는 상대 길이로 그린다(0으로 나누는 경우 방지).
        int maxDefect = s.DefectTypes.Count == 0 ? 0 : s.DefectTypes.Max(x => x.Count);
        foreach (var d in s.DefectTypes)
            DefectTypes.Add(new BarItem(d.Type, d.Count.ToString("N0"), maxDefect == 0 ? 0 : (double)d.Count / maxDefect));

        TimeHistogram.Clear();
        if (s.Time is { } t)
        {
            TimeText = $"평균 {t.MeanMs:F1} ms · p95 {t.P95Ms} ms · 최대 {t.MaxMs} ms  (OK/NG {t.Count:N0}건, 전처리+추론+판정)";
            int maxBucket = t.Histogram.Max(b => b.Count);
            foreach (var b in t.Histogram)
                TimeHistogram.Add(new BarItem(b.Label, b.Count.ToString("N0"), maxBucket == 0 ? 0 : (double)b.Count / maxBucket));
        }
        else TimeText = "처리시간 데이터 없음";

        // 안내 문구: 결과가 없을 때, 그리고 분류 모델은 불량 위치·유형을 내지 않아 "불량 유형"이 비는 이유를 설명한다.
        Message = s.Total == 0
            ? "해당 기간에 검사 결과가 없습니다."
            : s.DefectTypes.Count == 0
                ? "분류 모델은 불량 위치·유형을 내지 않아 '불량 유형' 항목이 비어 있습니다(검출 모델 전환 후 표시)."
                : "";
    }
}
