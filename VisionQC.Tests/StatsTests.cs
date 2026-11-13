using VisionQC.Core;
using VisionQC.Data;

namespace VisionQC.Tests;

public class StatsCalculatorTests
{
    private static readonly TimeZoneInfo Kst = TimeZoneInfo.CreateCustomTimeZone("KST", TimeSpan.FromHours(9), "KST", "KST");
    // DateTime.Parse는 'Z' 문자열을 로컬 시각으로 바꿔 버리므로 UTC로 고정해서 읽는다(테스트 도우미의 함정).
    private static DateTime Utc(string iso) => DateTime.Parse(iso, System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
    private static StatRow Row(string iso, Verdict v, int ms = 10) => new(Utc(iso), v, ms);
    private static StatsData Data(params StatRow[] rows) => new(rows, Array.Empty<DefectCount>());

    [Fact]
    public void Yield_excludes_error_from_denominator()
    {
        Assert.Equal(0.75, QualityMath.YieldRate(3, 1));
        Assert.Equal(1.0, QualityMath.YieldRate(5, 0));
        Assert.Null(QualityMath.YieldRate(0, 0));          // 판정된 것이 없으면 0%가 아니라 "없음"
    }

    [Fact]
    public void Live_screen_case_37_ok_13_ng_is_74_percent()
        => Assert.Equal(0.74, QualityMath.YieldRate(37, 13)!.Value, 6);   // 시연 캡처의 72%(한 건 늦게 갱신) 버그 회귀 방지

    [Fact]
    public void Summary_counts_and_yield()
    {
        var s = StatsCalculator.Build(Data(
            Row("2026-11-13T01:00:00Z", Verdict.OK), Row("2026-11-13T01:01:00Z", Verdict.OK),
            Row("2026-11-13T01:02:00Z", Verdict.NG), Row("2026-11-13T01:03:00Z", Verdict.ERROR)), Kst);
        Assert.Equal((4, 2, 1, 1), (s.Total, s.Ok, s.Ng, s.Error));
        Assert.Equal(2.0 / 3, s.YieldRate!.Value, 6);
    }

    [Fact]
    public void Daily_buckets_use_local_calendar_day_not_utc()
    {
        // UTC 11-12 15:30 = KST 11-13 00:30 (다음 날), UTC 11-12 14:59 = KST 11-12 23:59
        var s = StatsCalculator.Build(Data(
            Row("2026-11-12T14:59:00Z", Verdict.OK),
            Row("2026-11-12T15:30:00Z", Verdict.NG),
            Row("2026-11-14T00:00:00Z", Verdict.OK)), Kst);
        Assert.Equal(new[] { new DateOnly(2026, 11, 12), new DateOnly(2026, 11, 13), new DateOnly(2026, 11, 14) },
            s.Daily.Select(d => d.Date).ToArray());
        Assert.Equal(1, s.Daily[1].Ng);
        Assert.Equal(0.0, s.Daily[1].YieldRate);           // NG만 있는 날은 0%
    }

    [Fact]
    public void Day_with_only_errors_has_no_yield()
    {
        var s = StatsCalculator.Build(Data(Row("2026-11-13T01:00:00Z", Verdict.ERROR)), Kst);
        Assert.Null(s.Daily.Single().YieldRate);
        Assert.Null(s.YieldRate);
    }

    [Fact]
    public void Days_without_inspections_are_omitted_and_sorted()
    {
        var s = StatsCalculator.Build(Data(
            Row("2026-11-20T01:00:00Z", Verdict.OK), Row("2026-11-10T01:00:00Z", Verdict.OK)), Kst);
        Assert.Equal(2, s.Daily.Count);
        Assert.True(s.Daily[0].Date < s.Daily[1].Date);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(10, 10)]     // ceil(0.95*10)=10 번째
    [InlineData(20, 19)]     // ceil(0.95*20)=19 번째
    [InlineData(100, 95)]
    public void Percentile_is_nearest_rank(int n, int expected)
        => Assert.Equal(expected, StatsCalculator.Percentile(Enumerable.Range(1, n).ToArray(), 0.95));

    [Fact]
    public void Time_stats_exclude_error_rows_and_fill_histogram()
    {
        var s = StatsCalculator.Build(Data(
            Row("2026-11-13T01:00:00Z", Verdict.OK, 5),
            Row("2026-11-13T01:00:01Z", Verdict.NG, 15),
            Row("2026-11-13T01:00:02Z", Verdict.OK, 250),
            Row("2026-11-13T01:00:03Z", Verdict.ERROR, 1)), Kst);        // ERROR의 1ms는 분포에서 제외
        var t = s.Time!;
        Assert.Equal(3, t.Count);
        Assert.Equal(90.0, t.MeanMs, 6);
        Assert.Equal(250, t.MaxMs);
        Assert.Equal(250, t.P95Ms);
        Assert.Equal(3, t.Histogram.Sum(b => b.Count));
        Assert.Equal(1, t.Histogram.Single(b => b.Label == "<10ms").Count);
        Assert.Equal(1, t.Histogram.Single(b => b.Label == "10~20").Count);
        Assert.Equal(1, t.Histogram.Single(b => b.Label == "200ms+").Count);
    }

    [Fact]
    public void Empty_data_gives_zero_summary_without_exceptions()
    {
        var s = StatsCalculator.Build(Data(), Kst);
        Assert.Equal(0, s.Total);
        Assert.Null(s.YieldRate);
        Assert.Empty(s.Daily);
        Assert.Null(s.Time);
    }

    [Fact]
    public void Defect_types_sorted_by_count_desc()
    {
        var s = StatsCalculator.Build(new StatsData(Array.Empty<StatRow>(),
            new[] { new DefectCount("scratch", 2), new DefectCount("short", 5), new DefectCount("open", 2) }), Kst);
        Assert.Equal(new[] { "short", "open", "scratch" }, s.DefectTypes.Select(d => d.Type).ToArray());   // 동률은 이름순
    }
}

public class StatsRepositoryTests
{
    [Fact]
    public async Task Query_stats_filters_by_period_and_counts_defect_types()
    {
        using var d = new TempDir();
        var repo = new SqliteResultRepository(System.IO.Path.Combine(d.Path, "t.db"));
        var job = await repo.BeginJobAsync(new ModelInfo("m", "v", "c"), "s", 0.5f);
        var now = new DateTime(2026, 11, 13, 6, 0, 0, DateTimeKind.Utc);

        await repo.SaveAsync(new InspectionResult
        {
            JobId = job, ImagePath = "old.png", Verdict = Verdict.NG, ElapsedMs = 9, InspectedAtUtc = now.AddDays(-10),
            Defects = new[] { new Detection("scratch", 0.9f, 0, 0, 1, 1) }
        });
        await repo.SaveAsync(new InspectionResult
        {
            JobId = job, ImagePath = "a.png", Verdict = Verdict.NG, ElapsedMs = 20, InspectedAtUtc = now,
            Defects = new[] { new Detection("scratch", 0.9f, 0, 0, 1, 1), new Detection("short", 0.8f, 1, 1, 2, 2) }
        });
        await repo.SaveAsync(new InspectionResult { JobId = job, ImagePath = "b.png", Verdict = Verdict.OK, ElapsedMs = 30, InspectedAtUtc = now });

        var all = await repo.QueryStatsAsync(new StatsFilter());
        Assert.Equal(3, all.Rows.Count);
        Assert.Equal(2, all.Defects.Single(x => x.Type == "scratch").Count);

        var recent = await repo.QueryStatsAsync(new StatsFilter(FromUtc: now.AddDays(-1)));
        Assert.Equal(2, recent.Rows.Count);
        Assert.Equal(1, recent.Defects.Single(x => x.Type == "scratch").Count);   // 기간 밖 불량은 세지 않는다
        Assert.Equal(1, recent.Defects.Single(x => x.Type == "short").Count);

        var none = await repo.QueryStatsAsync(new StatsFilter(FromUtc: now.AddDays(1)));
        Assert.Empty(none.Rows);
        Assert.Empty(none.Defects);

        var s = StatsCalculator.Build(recent, TimeZoneInfo.Utc);
        Assert.Equal(1.0 / 2, s.YieldRate!.Value);
    }
}
