// ============================================================================
// Stats.cs - 검사 통계(양품률, 일별 추이, 불량 유형, 처리시간 분포) 계산
//
// 파이프라인에서의 위치: 저장소(IResultRepository.QueryStatsAsync)가 최소 컬럼만 담은 원본 행을 주면,
// 이 파일의 순수 계산 코드(StatsCalculator)가 요약을 만든다. 계산을 저장소·UI와 분리해 DB 없이 단위 테스트할 수 있다.
// 용어: 양품률(Yield) = 양품 비율. 처리시간 p95 = 전체의 95%가 이 시간 이내에 끝난다는 뜻(느린 꼬리를 보는 지표).
// ============================================================================
namespace VisionQC.Core;

/// <summary>통계 조회 기간(UTC). null 이면 해당 방향 제한 없음.</summary>
public sealed record StatsFilter(DateTime? FromUtc = null, DateTime? ToUtc = null);

/// <summary>통계 계산에 필요한 최소 컬럼만 담은 원본 행.</summary>
public sealed record StatRow(DateTime InspectedAtUtc, Verdict Verdict, int ElapsedMs);

/// <summary>불량 유형별 검출 건수(검출 모델에서 의미가 있음).</summary>
public sealed record DefectCount(string Type, int Count);

/// <summary>저장소가 돌려주는 통계 원본: 행 목록 + 불량 유형별 건수. 집계는 StatsCalculator 가 한다.</summary>
public sealed record StatsData(IReadOnlyList<StatRow> Rows, IReadOnlyList<DefectCount> Defects);

/// <summary>하루치 집계(전체/OK/NG/ERROR 건수). 날짜는 지정한 시간대의 달력 날짜.</summary>
public sealed record DailyStat(DateOnly Date, int Total, int Ok, int Ng, int Error)
{
    /// <summary>그날의 양품률. 판정된 건이 없으면 null.</summary>
    public double? YieldRate => QualityMath.YieldRate(Ok, Ng);
}

/// <summary>처리시간 히스토그램의 막대 1개(구간 이름, 건수).</summary>
public sealed record TimeBucket(string Label, int Count);

/// <summary>처리시간 통계. OK/NG만 대상(ERROR는 실패가 빨라 분포를 왜곡하므로 제외).</summary>
public sealed record TimeStats(int Count, double MeanMs, int P95Ms, int MaxMs, IReadOnlyList<TimeBucket> Histogram);

/// <summary>통계 화면에 필요한 모든 요약: 전체·판정별 건수, 양품률, 일별 추이, 불량 유형 순위, 처리시간(없으면 null).</summary>
public sealed record StatsSummary(
    int Total, int Ok, int Ng, int Error, double? YieldRate,
    IReadOnlyList<DailyStat> Daily, IReadOnlyList<DefectCount> DefectTypes, TimeStats? Time);

/// <summary>품질 지표 계산 모음.</summary>
public static class QualityMath
{
    /// <summary>양품률 = OK / (OK + NG). 판정 불능(ERROR)은 분모에서 뺀다. 판정된 것이 없으면 null.</summary>
    public static double? YieldRate(int ok, int ng)
    {
        // ERROR 는 불량도 양품도 아닌 "판정 불능"이라 분모에서 뺀다. 포함하면 이미지 읽기 오류가 양품률을 왜곡한다.
        int judged = ok + ng;
        return judged == 0 ? null : (double)ok / judged;
    }
}

/// <summary>원본 통계 행을 화면용 요약으로 바꾸는 순수 계산기(DB·UI 의존 없음).</summary>
public static class StatsCalculator
{
    // 처리시간 히스토그램 구간 경계. 각 구간은 "미만(상한 제외)"이며 마지막은 상한 없음.
    private static readonly (string Label, int UpperExclusive)[] Edges =
    {
        ("<10ms", 10), ("10~20", 20), ("20~50", 50), ("50~100", 100), ("100~200", 200), ("200ms+", int.MaxValue)
    };

    /// <summary>일 단위는 지정한 시간대의 달력 날짜 기준(저장은 UTC). 검사가 없던 날은 표시하지 않는다.</summary>
    public static StatsSummary Build(StatsData data, TimeZoneInfo tz)
    {
        // 판정별 건수 집계.
        var rows = data.Rows;
        int ok = rows.Count(r => r.Verdict == Verdict.OK);
        int ng = rows.Count(r => r.Verdict == Verdict.NG);
        int err = rows.Count(r => r.Verdict == Verdict.ERROR);

        // 일별 집계: 저장된 UTC 시각을 지정 시간대(예: 한국)의 달력 날짜로 바꿔 묶는다(자정 기준이 현지 날짜와 맞게). 날짜순 정렬.
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

    // 처리시간 통계. ERROR 는 실패해서 빨리 끝나는 경우가 많아 분포를 왜곡하므로 제외한다. 대상이 없으면 null.
    private static TimeStats? BuildTime(IReadOnlyList<StatRow> rows)
    {
        var ms = rows.Where(r => r.Verdict != Verdict.ERROR).Select(r => r.ElapsedMs).OrderBy(x => x).ToArray();
        if (ms.Length == 0) return null;

        var hist = new int[Edges.Length];
        // 각 값을 처음으로 "상한 미만"인 구간에 넣는다(마지막 구간은 상한이 int.MaxValue 라 항상 들어간다).
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
        // 예) p=0.95, n=100 이면 rank=95 -> 95번째로 작은 값. Clamp 는 p=0 이나 부동소수점 오차로 순위가 범위를 벗어나는 것을 막는다.
        int rank = (int)Math.Ceiling(p * sortedAscending.Length);
        return sortedAscending[Math.Clamp(rank, 1, sortedAscending.Length) - 1];
    }

    // DB 에서 읽은 시각은 Kind 가 Unspecified 일 수 있다. 저장 규약이 UTC 이므로 Unspecified 는 UTC 로 간주해 시간대 변환이 어긋나지 않게 한다.
    private static DateTime AsUtc(DateTime t) => t.Kind switch
    {
        DateTimeKind.Utc => t,
        DateTimeKind.Local => t.ToUniversalTime(),
        _ => DateTime.SpecifyKind(t, DateTimeKind.Utc)
    };
}
