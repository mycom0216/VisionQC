namespace VisionQC.Core;

/// <summary>엔지니어의 정정 1건. 원 판정은 덮어쓰지 않고 행을 추가한다(가장 나중 정정이 유효).</summary>
public sealed record Review(long Id, long ResultId, string Reviewer, Verdict Corrected, string? Note, DateTime ReviewedAtUtc);

public sealed record ReviewedResult(InspectionResult Result, Review? Latest, int ReviewCount)
{
    /// <summary>정정이 있으면 정정한 판정, 없으면 원 판정.</summary>
    public Verdict EffectiveVerdict => Latest?.Corrected ?? Result.Verdict;

    /// <summary>정정한 판정이 원 판정과 다르면 오판 후보(재학습·임계값 조정 자료).</summary>
    public bool IsMisjudged => Latest is { } l && l.Corrected != Result.Verdict;
}

/// <summary>임계값을 바꿨을 때 판정이 어떻게 달라지는지(저장된 점수로 계산, 모델을 다시 돌리지 않는다).</summary>
public sealed record RejudgePreview(int Judged, int ToNg, int ToOk, int Unchanged, int ErrorSkipped);

/// <summary>정정된 건을 정답으로 보고 계산한 혼동행렬. NG(불량)를 양성으로 본다.</summary>
public sealed record TruthEvaluation(int Tp, int Fn, int Fp, int Tn)
{
    public int Samples => Tp + Fn + Fp + Tn;
    /// <summary>재현율 = 불량 중 잡은 비율. 정정된 불량이 없으면 null.</summary>
    public double? Recall => Tp + Fn == 0 ? null : (double)Tp / (Tp + Fn);
    /// <summary>정밀도 = NG 판정 중 진짜 불량 비율. NG 판정이 없으면 null.</summary>
    public double? Precision => Tp + Fp == 0 ? null : (double)Tp / (Tp + Fp);
}

public static class Rejudge
{
    /// <summary>저장된 점수로 새 임계값을 적용한 판정. ThresholdJudge와 같은 규칙(점수 ≥ 임계값이면 NG). ERROR는 점수가 없으므로 그대로.</summary>
    public static Verdict Apply(Verdict original, float score, float threshold)
        => original == Verdict.ERROR ? Verdict.ERROR : (score >= threshold ? Verdict.NG : Verdict.OK);

    public static RejudgePreview Preview(IEnumerable<InspectionResult> rows, float threshold)
    {
        int judged = 0, toNg = 0, toOk = 0, same = 0, skipped = 0;
        foreach (var r in rows)
        {
            if (r.Verdict == Verdict.ERROR) { skipped++; continue; }
            judged++;
            var now = Apply(r.Verdict, r.Score, threshold);
            if (now == r.Verdict) same++;
            else if (now == Verdict.NG) toNg++;
            else toOk++;
        }
        return new RejudgePreview(judged, toNg, toOk, same, skipped);
    }

    /// <summary>정정된 건만으로 새 임계값의 성능을 계산한다. 정정이 없으면 표본 0.</summary>
    public static TruthEvaluation EvaluateAgainstCorrections(IEnumerable<ReviewedResult> rows, float threshold)
    {
        int tp = 0, fn = 0, fp = 0, tn = 0;
        foreach (var r in rows)
        {
            if (r.Latest is not { } rev) continue;
            // ERROR 결과는 점수가 없어 임계값 평가에서 제외
            if (r.Result.Verdict == Verdict.ERROR) continue;
            bool truthNg = rev.Corrected == Verdict.NG;
            bool predNg = r.Result.Score >= threshold;
            if (truthNg) { if (predNg) tp++; else fn++; }
            else { if (predNg) fp++; else tn++; }
        }
        return new TruthEvaluation(tp, fn, fp, tn);
    }
}

public sealed class ReviewService
{
    private readonly IResultRepository _repo;
    public ReviewService(IResultRepository repo) => _repo = repo;

    public const int MaxNoteLength = 500;

    /// <summary>정정을 기록한다. 정정 값은 OK/NG만(ERROR로 정정할 수 없음).</summary>
    public Task CorrectAsync(long resultId, Verdict corrected, string? note, string reviewer, CancellationToken ct = default)
    {
        if (corrected == Verdict.ERROR)
            throw new ArgumentException("정정은 OK 또는 NG만 가능합니다.", nameof(corrected));
        if (string.IsNullOrWhiteSpace(reviewer))
            throw new ArgumentException("정정한 사람이 필요합니다.", nameof(reviewer));
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (note is { Length: > MaxNoteLength })
            throw new ArgumentException($"메모는 {MaxNoteLength}자 이하여야 합니다.", nameof(note));

        return _repo.AddReviewAsync(new Review(0, resultId, reviewer.Trim(), corrected, note, DateTime.UtcNow), ct);
    }
}
