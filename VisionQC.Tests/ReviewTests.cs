using Microsoft.Data.Sqlite;
using VisionQC.Core;
using VisionQC.Data;

namespace VisionQC.Tests;

public class RejudgeTests
{
    private static InspectionResult R(Verdict v, float score, long id = 1)
        => new() { Id = id, ImagePath = $"{id}.png", Verdict = v, Score = score };

    [Theory]
    [InlineData(Verdict.OK, 0.49f, 0.5f, Verdict.OK)]
    [InlineData(Verdict.OK, 0.50f, 0.5f, Verdict.NG)]    // 경계값은 NG (ThresholdJudge와 동일)
    [InlineData(Verdict.NG, 0.61f, 0.7f, Verdict.OK)]
    [InlineData(Verdict.ERROR, 0.99f, 0.1f, Verdict.ERROR)]   // 점수가 없는 ERROR는 임계값을 바꿔도 ERROR
    public void Apply_matches_threshold_rule(Verdict orig, float score, float thr, Verdict expected)
        => Assert.Equal(expected, Rejudge.Apply(orig, score, thr));

    [Fact]
    public void Apply_agrees_with_ThresholdJudge()
    {
        var judge = new ThresholdJudge();
        foreach (var score in new[] { 0f, 0.3f, 0.5f, 0.678f, 0.99f })
            foreach (var thr in new[] { 0.1f, 0.5f, 0.678f, 0.9f })
            {
                var j = judge.Decide(InferenceOutput.FromScore(score), new InspectionSettings { Threshold = thr });
                Assert.Equal(j.Verdict, Rejudge.Apply(j.Verdict, score, thr));
            }
    }

    [Fact]
    public void Preview_counts_flips_and_skips_errors()
    {
        var rows = new[]
        {
            R(Verdict.OK, 0.61f, 1),   // 0.678 → OK. 임계값 0.5로 내리면 NG로 바뀜
            R(Verdict.OK, 0.01f, 2),
            R(Verdict.NG, 0.99f, 3),
            R(Verdict.NG, 0.70f, 4),   // 임계값 0.8로 올리면 OK로 바뀜
            R(Verdict.ERROR, 0f, 5),
        };
        var p = Rejudge.Preview(rows, 0.5f);
        Assert.Equal((4, 1, 0, 3, 1), (p.Judged, p.ToNg, p.ToOk, p.Unchanged, p.ErrorSkipped));

        var q = Rejudge.Preview(rows, 0.8f);
        Assert.Equal((4, 0, 1, 3, 1), (q.Judged, q.ToNg, q.ToOk, q.Unchanged, q.ErrorSkipped));
    }

    private static ReviewedResult Rv(Verdict original, float score, Verdict? corrected, long id)
        => new(R(original, score, id),
               corrected is { } c ? new Review(id, id, "eng", c, null, DateTime.UtcNow) : null,
               corrected is null ? 0 : 1);

    [Fact]
    public void Truth_evaluation_uses_only_corrected_rows_and_non_error()
    {
        var rows = new[]
        {
            Rv(Verdict.OK, 0.61f, Verdict.NG, 1),   // 실제 불량인데 놓침(0.678 기준). 0.5면 잡음
            Rv(Verdict.NG, 0.99f, Verdict.NG, 2),
            Rv(Verdict.OK, 0.02f, Verdict.OK, 3),
            Rv(Verdict.NG, 0.80f, Verdict.OK, 4),   // 실제 양품인데 NG(오검출)
            Rv(Verdict.NG, 0.95f, null, 5),         // 정정 없음 → 제외
            Rv(Verdict.ERROR, 0f, Verdict.NG, 6),   // ERROR → 제외
        };
        var at678 = Rejudge.EvaluateAgainstCorrections(rows, 0.678f);
        Assert.Equal((1, 1, 1, 1), (at678.Tp, at678.Fn, at678.Fp, at678.Tn));
        Assert.Equal(4, at678.Samples);
        Assert.Equal(0.5, at678.Recall);
        Assert.Equal(0.5, at678.Precision);

        var at50 = Rejudge.EvaluateAgainstCorrections(rows, 0.5f);
        Assert.Equal(1.0, at50.Recall);                      // 임계값을 내리면 놓친 불량을 잡는다
        Assert.Equal(2.0 / 3, at50.Precision!.Value, 6);     // 대신 오검출이 그대로
    }

    [Fact]
    public void Truth_evaluation_without_corrections_has_no_rates()
    {
        var e = Rejudge.EvaluateAgainstCorrections(new[] { Rv(Verdict.OK, 0.1f, null, 1) }, 0.5f);
        Assert.Equal(0, e.Samples);
        Assert.Null(e.Recall);
        Assert.Null(e.Precision);
    }

    [Fact]
    public void ReviewedResult_effective_verdict_and_misjudged_flag()
    {
        var plain = Rv(Verdict.OK, 0.1f, null, 1);
        Assert.Equal(Verdict.OK, plain.EffectiveVerdict);
        Assert.False(plain.IsMisjudged);

        var wrong = Rv(Verdict.OK, 0.6f, Verdict.NG, 2);
        Assert.Equal(Verdict.NG, wrong.EffectiveVerdict);
        Assert.True(wrong.IsMisjudged);

        var confirmed = Rv(Verdict.NG, 0.9f, Verdict.NG, 3);   // 원 판정이 맞다고 확인
        Assert.False(confirmed.IsMisjudged);
    }
}

public class ReviewServiceTests
{
    [Fact]
    public async Task Correct_records_trimmed_note_and_reviewer()
    {
        var repo = new MemoryRepo();
        await new ReviewService(repo).CorrectAsync(7, Verdict.NG, "  휘어짐  ", " kim ");
        var r = Assert.Single(repo.Reviews);
        Assert.Equal((7L, "kim", Verdict.NG, "휘어짐"), (r.ResultId, r.Reviewer, r.Corrected, r.Note));
    }

    [Fact]
    public async Task Blank_note_is_stored_as_null()
    {
        var repo = new MemoryRepo();
        await new ReviewService(repo).CorrectAsync(1, Verdict.OK, "   ", "kim");
        Assert.Null(repo.Reviews.Single().Note);
    }

    [Fact]
    public async Task Rejects_error_verdict_blank_reviewer_and_long_note()
    {
        var svc = new ReviewService(new MemoryRepo());
        await Assert.ThrowsAsync<ArgumentException>(() => svc.CorrectAsync(1, Verdict.ERROR, null, "kim"));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.CorrectAsync(1, Verdict.NG, null, "  "));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.CorrectAsync(1, Verdict.NG, new string('x', 501), "kim"));
        await svc.CorrectAsync(1, Verdict.NG, new string('x', 500), "kim");   // 경계값은 허용
    }
}

public class ReviewRepositoryTests
{
    private static async Task<(SqliteResultRepository repo, TempDir dir, long jobId)> Setup()
    {
        var d = new TempDir();
        var repo = new SqliteResultRepository(System.IO.Path.Combine(d.Path, "t.db"));
        var job = await repo.BeginJobAsync(new ModelInfo("m", "v", "c"), "s", 0.5f);
        return (repo, d, job);
    }

    private static async Task<long> Save(SqliteResultRepository repo, long job, string name, Verdict v, float score)
    {
        await repo.SaveAsync(new InspectionResult { JobId = job, ImagePath = name, Verdict = v, Score = score, InspectedAtUtc = DateTime.UtcNow });
        return (await repo.QueryAsync(new HistoryFilter())).Single(r => r.ImagePath == name).Id;
    }

    [Fact]
    public async Task Latest_review_wins_and_original_verdict_is_untouched()
    {
        var (repo, dir, job) = await Setup();
        using var _ = dir;
        var id = await Save(repo, job, "a.png", Verdict.OK, 0.61f);

        await repo.AddReviewAsync(new Review(0, id, "kim", Verdict.NG, "휘어짐", DateTime.UtcNow.AddMinutes(-5)));
        await repo.AddReviewAsync(new Review(0, id, "lee", Verdict.OK, "재확인: 양품", DateTime.UtcNow));

        var row = (await repo.QueryReviewedAsync(new HistoryFilter())).Single();
        Assert.Equal(Verdict.OK, row.Result.Verdict);                // 원 판정 그대로
        Assert.Equal((Verdict.OK, "lee", 2), (row.Latest!.Corrected, row.Latest.Reviewer, row.ReviewCount));
        Assert.False(row.IsMisjudged);                               // 마지막 정정이 원 판정과 같음

        var history = await repo.GetReviewsAsync(id);
        Assert.Equal(new[] { "kim", "lee" }, history.Select(h => h.Reviewer).ToArray());   // 오래된 순
        Assert.Equal("휘어짐", history[0].Note);
    }

    [Fact]
    public async Task Korean_note_and_reviewer_roundtrip_unchanged()
    {
        var (repo, dir, job) = await Setup();
        using var _ = dir;
        var id = await Save(repo, job, "한글.png", Verdict.OK, 0.61f);
        await repo.AddReviewAsync(new Review(0, id, "홍길동", Verdict.NG, "휘어진 리드 확인", DateTime.UtcNow));
        var r = (await repo.GetReviewsAsync(id)).Single();
        Assert.Equal(("홍길동", "휘어진 리드 확인"), (r.Reviewer, r.Note));
    }

    [Fact]
    public async Task Rows_without_reviews_have_null_latest()
    {
        var (repo, dir, job) = await Setup();
        using var _ = dir;
        var a = await Save(repo, job, "a.png", Verdict.NG, 0.9f);
        await Save(repo, job, "b.png", Verdict.OK, 0.1f);
        await repo.AddReviewAsync(new Review(0, a, "kim", Verdict.NG, null, DateTime.UtcNow));

        var rows = await repo.QueryReviewedAsync(new HistoryFilter());
        Assert.Equal(2, rows.Count);
        Assert.Null(rows.Single(r => r.Result.ImagePath == "b.png").Latest);
        Assert.Equal(0, rows.Single(r => r.Result.ImagePath == "b.png").ReviewCount);
        Assert.Equal(1, rows.Single(r => r.Result.ImagePath == "a.png").ReviewCount);
    }

    [Fact]
    public async Task Review_of_unknown_result_is_rejected_by_foreign_key()
    {
        var (repo, dir, _) = await Setup();
        using var _d = dir;
        await Assert.ThrowsAsync<SqliteException>(() =>
            repo.AddReviewAsync(new Review(0, 9999, "kim", Verdict.NG, null, DateTime.UtcNow)));
    }

    [Fact]
    public async Task Error_as_corrected_verdict_is_rejected()
    {
        var (repo, dir, job) = await Setup();
        using var _ = dir;
        var id = await Save(repo, job, "a.png", Verdict.OK, 0.1f);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            repo.AddReviewAsync(new Review(0, id, "kim", Verdict.ERROR, null, DateTime.UtcNow)));
    }

    [Fact]
    public async Task Query_reviewed_respects_verdict_filter_and_empty_result()
    {
        var (repo, dir, job) = await Setup();
        using var _ = dir;
        Assert.Empty(await repo.QueryReviewedAsync(new HistoryFilter()));
        await Save(repo, job, "a.png", Verdict.NG, 0.9f);
        await Save(repo, job, "b.png", Verdict.OK, 0.1f);
        var ng = await repo.QueryReviewedAsync(new HistoryFilter(Verdict: Verdict.NG));
        Assert.Equal("a.png", ng.Single().Result.ImagePath);
    }

    [Fact]
    public async Task Existing_database_without_review_table_gets_it_on_open()
    {
        using var d = new TempDir();
        var path = System.IO.Path.Combine(d.Path, "old.db");
        // 통계 단계까지의 구버전 스키마만 있는 DB를 흉내
        using (var c = new SqliteConnection($"Data Source={path}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE inspection_job(id INTEGER PRIMARY KEY, model_name TEXT NOT NULL, model_version TEXT NOT NULL, source_name TEXT NOT NULL, status TEXT NOT NULL, threshold REAL NOT NULL, started_at TEXT NOT NULL, ended_at TEXT NULL);"
                            + "CREATE TABLE inspection_result(id INTEGER PRIMARY KEY, job_id INTEGER NOT NULL, image_path TEXT NOT NULL, verdict TEXT NOT NULL, score REAL NOT NULL, elapsed_ms INTEGER NOT NULL, error_message TEXT NULL, inspected_at TEXT NOT NULL);";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var repo = new SqliteResultRepository(path);
        Assert.Empty(await repo.QueryReviewedAsync(new HistoryFilter()));   // 예외 없이 열리고 새 테이블이 생김
        Assert.Empty(await repo.GetReviewsAsync(1));
    }
}
