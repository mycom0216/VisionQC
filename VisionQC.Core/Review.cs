// ============================================================================
// Review.cs - 사람의 판정 정정(Review)과 임계값 재평가 계산
//
// 파이프라인에서의 위치: 모델이 내린 원 판정을 엔지니어가 사후에 검토해 "실제로는 OK/NG였다"고 고치는 단계.
// 설계 이유:
//  - 원 판정은 절대 덮어쓰지 않고 정정을 "행 추가"로 쌓는다(감사 가능성, 모델 성능 추적). 가장 나중 정정이 유효.
//  - 정정된 결과는 "정답 라벨"이 되어, 임계값을 바꾸면 성능이 어떻게 달라지는지(재현율·정밀도)를 모델 재실행 없이 계산할 수 있다.
//  - 용어: 오판 = 원 판정과 정정이 다른 경우. TP/FN/FP/TN = 혼동행렬(불량을 양성으로 볼 때 맞춤/놓침/오검출/정상통과).
// ============================================================================
namespace VisionQC.Core;

/// <summary>엔지니어의 정정 1건. 원 판정은 덮어쓰지 않고 행을 추가한다(가장 나중 정정이 유효).</summary>
public sealed record Review(long Id, long ResultId, string Reviewer, Verdict Corrected, string? Note, DateTime ReviewedAtUtc);

/// <summary>검사 결과 1건 + 그 결과의 가장 나중 정정(없으면 null) + 정정 횟수. 이력 화면에서 "원 판정 vs 정정 판정"을 함께 보여 주는 데 쓴다.</summary>
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
// Tp = 불량을 NG로 맞춤, Fn = 불량을 놓침(미검출, 가장 위험), Fp = 양품을 NG로 오검출, Tn = 양품을 OK로 맞춤.
public sealed record TruthEvaluation(int Tp, int Fn, int Fp, int Tn)
{
    /// <summary>평가에 쓴 표본(정정된 건) 수.</summary>
    public int Samples => Tp + Fn + Fp + Tn;
    /// <summary>재현율 = 불량 중 잡은 비율. 정정된 불량이 없으면 null.</summary>
    public double? Recall => Tp + Fn == 0 ? null : (double)Tp / (Tp + Fn);
    /// <summary>정밀도 = NG 판정 중 진짜 불량 비율. NG 판정이 없으면 null.</summary>
    public double? Precision => Tp + Fp == 0 ? null : (double)Tp / (Tp + Fp);
}

/// <summary>임계값을 바꿔 보는 "what-if" 계산 모음. 저장된 점수만 쓰므로 모델을 다시 돌리지 않아 빠르고 안전하다(저장소도 바뀌지 않는다).</summary>
public static class Rejudge
{
    /// <summary>저장된 점수로 새 임계값을 적용한 판정. ThresholdJudge와 같은 규칙(점수 ≥ 임계값이면 NG). ERROR는 점수가 없으므로 그대로.</summary>
    public static Verdict Apply(Verdict original, float score, float threshold)
        => original == Verdict.ERROR ? Verdict.ERROR : (score >= threshold ? Verdict.NG : Verdict.OK);

    /// <summary>rows 의 저장된 점수에 새 임계값을 적용했을 때 판정이 몇 건이나 바뀌는지 센다(ToNg/ToOk = 바뀐 방향별 건수). ERROR 건은 점수가 없어 건너뛴다.</summary>
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
    // 정정이 없는 건은 정답을 알 수 없어 제외. 예측(predNg)은 "저장된 점수 >= 새 임계값", 정답(truthNg)은 정정 판정이다.
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

/// <summary>정정 기록 서비스. 입력 검증(정정값·작성자·메모 길이)을 한 뒤 저장소에 정정 1건을 추가한다.</summary>
public sealed class ReviewService
{
    private readonly IResultRepository _repo;
    public ReviewService(IResultRepository repo) => _repo = repo;

    // 메모 최대 길이 500자(DB·화면 보호용 상한).
    public const int MaxNoteLength = 500;

    /// <summary>정정을 기록한다. 정정 값은 OK/NG만(ERROR로 정정할 수 없음).</summary>
    public Task CorrectAsync(long resultId, Verdict corrected, string? note, string reviewer, CancellationToken ct = default)
    {
        if (corrected == Verdict.ERROR)
            throw new ArgumentException("정정은 OK 또는 NG만 가능합니다.", nameof(corrected));
        if (string.IsNullOrWhiteSpace(reviewer))
            throw new ArgumentException("정정한 사람이 필요합니다.", nameof(reviewer));
        // 공백뿐인 메모는 없는 것(null)으로 정리하고 앞뒤 공백을 제거해 저장한다.
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (note is { Length: > MaxNoteLength })
            throw new ArgumentException($"메모는 {MaxNoteLength}자 이하여야 합니다.", nameof(note));

        return _repo.AddReviewAsync(new Review(0, resultId, reviewer.Trim(), corrected, note, DateTime.UtcNow), ct);
    }
}
