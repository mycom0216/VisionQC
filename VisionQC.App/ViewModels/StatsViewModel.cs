using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisionQC.Core;

namespace VisionQC.App.ViewModels;

/// <summary>막대 한 줄. Fraction은 0~1(ProgressBar로 그린다).</summary>
public sealed record BarItem(string Label, string ValueText, double Fraction);

public partial class StatsViewModel : ObservableObject
{
    private readonly IResultRepository _repo;
    public StatsViewModel(IResultRepository repo) => _repo = repo;

    [ObservableProperty] private DateTime? _fromDate;
    [ObservableProperty] private DateTime? _toDate;
    [ObservableProperty] private string _message = "";

    [ObservableProperty] private string _totalText = "-";
    [ObservableProperty] private string _okText = "-";
    [ObservableProperty] private string _ngText = "-";
    [ObservableProperty] private string _errorText = "-";
    [ObservableProperty] private string _yieldText = "-";
    [ObservableProperty] private string _timeText = "처리시간 데이터 없음";

    public ObservableCollection<BarItem> Daily { get; } = new();
    public ObservableCollection<BarItem> DefectTypes { get; } = new();
    public ObservableCollection<BarItem> TimeHistogram { get; } = new();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            // 입력한 날짜는 로컬 기준. To는 그날 끝까지 포함.
            DateTime? from = FromDate is { } f ? f.Date.ToUniversalTime() : null;
            DateTime? to = ToDate is { } t ? t.Date.AddDays(1).AddTicks(-1).ToUniversalTime() : null;

            var data = await _repo.QueryStatsAsync(new StatsFilter(from, to));
            Apply(StatsCalculator.Build(data, TimeZoneInfo.Local));
        }
        catch (Exception ex) { Message = $"통계 조회 실패: {ex.Message}"; }
    }

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

        Message = s.Total == 0
            ? "해당 기간에 검사 결과가 없습니다."
            : s.DefectTypes.Count == 0
                ? "분류 모델은 불량 위치·유형을 내지 않아 '불량 유형' 항목이 비어 있습니다(검출 모델 전환 후 표시)."
                : "";
    }
}
