namespace VisionQC.Core;

public sealed record StatsFilter(DateTime? FromUtc = null, DateTime? ToUtc = null);

/// <summary>통계 계산에 필요한 최소 컬럼만 담은 원본 행.</summary>
public sealed record StatRow(DateTime InspectedAtUtc, Verdict Verdict, int ElapsedMs);

public sealed record DefectCount(string Type, int Count);

public sealed record StatsData(IReadOnlyList<StatRow> Rows, IReadOnlyList<DefectCount> Defects);

public sealed record DailyStat(DateOnly Date, int Total, int Ok, int Ng, int Error)
{
    public double? YieldRate => QualityMath.YieldRate(Ok, Ng);
}

public sealed record TimeBucket(string Label, int Count);

/// <summary>처리시간 통계. OK/NG만 대상(ERROR는 실패가 빨라 분포를 왜곡하므로 제외).</summary>
public sealed record TimeStats(int Count, double MeanMs, int P95Ms, int MaxMs, IReadOnlyList<TimeBucket> Histogram);

public sealed record StatsSummary(
    int Total, int Ok, int Ng, int Error, double? YieldRate,
    IReadOnlyList<DailyStat> Daily, IReadOnlyList<DefectCount> DefectTypes, TimeStats? Time);

public static class QualityMath
{
    /// <summary>양품률 = OK / (OK + NG). 판정 불능(ERROR)은 분모에서 뺀다. 판정된 것이 없으면 null.</summary>
    public static double? YieldRate(int ok, int ng)
    {
        int judged = ok + ng;
        return judged == 0 ? null : (double)ok / judged;
    }
}

public static class StatsCalculator
{
    private static readonly (string Label, int UpperExclusive)[] Edges =
    {
        ("<10ms", 10), ("10~20", 20), ("20~50", 50), ("50~100", 100), ("100~200", 200), ("200ms+", int.MaxValue)
    };

    /// <summary>일 단위는 지정한 시간대의 달력 날짜 기준(저장은 UTC). 검사가 없던 날은 표시하지 않는다.</summary>
    public static StatsSummary Build(StatsData data, TimeZoneInfo tz)
    {
        var rows = data.Rows;
        int ok = rows.Count(r => r.Verdict == Verdict.OK);
        int ng = rows.Count(r => r.Verdict == Verdict.NG);
        int err = rows.Count(r => r.Verdict == Verdict.ERROR);

        var daily = rows
            .GroupBy(r => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(AsUtc(r.InspectedAtUtc), tz)))
            .OrderBy(g => g.Key)
            .Select(g => new DailyStat(g.Key, g.Count(),
                g.Count(r => r.Verdict == Verdict.OK), g.Count(r => r.Verdict == Verdict.NG),
                g.Count(r => r.Verdict == Verdict.ERROR)))
            .ToList();

        return new StatsSummary(rows.Count, ok, ng, err, QualityMath.YieldRate(ok, ng), daily,
            data.Defects.OrderByDescending(d => d.Count).ThenBy(d => d.Type, StringComparer.Ordinal).ToList(),
            BuildTime(rows));
    }

    private static TimeStats? BuildTime(IReadOnlyList<StatRow> rows)
    {
        var ms = rows.Where(r => r.Verdict != Verdict.ERROR).Select(r => r.ElapsedMs).OrderBy(x => x).ToArray();
        if (ms.Length == 0) return null;

        var hist = new int[Edges.Length];
        foreach (var v in ms)
            for (int i = 0; i < Edges.Length; i++)
                if (v < Edges[i].UpperExclusive) { hist[i]++; break; }

        return new TimeStats(ms.Length, ms.Average(), Percentile(ms, 0.95), ms[^1],
            Edges.Select((e, i) => new TimeBucket(e.Label, hist[i])).ToList());
    }

    /// <summary>최근접 순위(nearest-rank) 백분위: 정렬한 n개 중 ceil(p·n)번째 값. 보간하지 않는다.</summary>
    public static int Percentile(int[] sortedAscending, double p)
    {
        if (sortedAscending.Length == 0) throw new ArgumentException("빈 배열");
        int rank = (int)Math.Ceiling(p * sortedAscending.Length);
        return sortedAscending[Math.Clamp(rank, 1, sortedAscending.Length) - 1];
    }

    private static DateTime AsUtc(DateTime t) => t.Kind switch
    {
        DateTimeKind.Utc => t,
        DateTimeKind.Local => t.ToUniversalTime(),
        _ => DateTime.SpecifyKind(t, DateTimeKind.Utc)
    };
}
