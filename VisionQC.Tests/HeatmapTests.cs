using Microsoft.Data.Sqlite;
using VisionQC.Core;
using VisionQC.Data;
using VisionQC.Inference;

namespace VisionQC.Tests;

public class HeatmapMathTests
{
    private static Heatmap Map(params float[] v) => new(3, 2, v);

    [Fact]
    public void Normalize_keeps_only_positive_evidence_scaled_to_max_one()
    {
        var n = HeatmapMath.Normalize(Map(-4f, 0f, 2f, 4f, 1f, -1f));
        Assert.Equal(new[] { 0f, 0f, 0.5f, 1f, 0.25f, 0f }, n);
    }

    [Theory]
    [InlineData(new float[] { -1f, -2f, 0f, -3f, -1f, -1f })]
    [InlineData(new float[] { 0f, 0f, 0f, 0f, 0f, 0f })]
    [InlineData(new float[] { float.NaN, float.NegativeInfinity, 0f, 0f, 0f, 0f })]
    public void No_positive_evidence_gives_all_zero_and_no_peak(float[] values)
    {
        var h = Map(values);
        Assert.All(HeatmapMath.Normalize(h), v => Assert.Equal(0f, v));
        Assert.Null(HeatmapMath.Peak(h));
    }

    [Fact]
    public void Peak_returns_column_and_row_of_strongest_cell()
    {
        // 3열×2행, 값 5가 (열2, 행1)
        Assert.Equal((2, 1), HeatmapMath.Peak(Map(1f, 2f, 3f, 4f, 0f, 5f)));
        Assert.Equal((0, 0), HeatmapMath.Peak(new Heatmap(1, 1, new[] { 0.1f })));
    }

    [Fact]
    public void Serialize_roundtrip_preserves_values_and_region()
    {
        var h = new Heatmap(7, 7, Enumerable.Range(0, 49).Select(i => i * 0.5f - 10f).ToArray(), 0.1, 0.2, 0.5, 0.6);
        var bytes = HeatmapMath.Serialize(h);
        Assert.Equal(4 + 8 + 16 + 49 * 4, bytes.Length);                       // 약 224바이트 — 결과 1건당 DB 증가분
        var back = HeatmapMath.TryDeserialize(bytes)!;
        Assert.Equal((7, 7), (back.Width, back.Height));
        Assert.Equal(h.Values, back.Values);
        Assert.Equal((0.1, 0.2, 0.5, 0.6), (Math.Round(back.RegionX, 5), Math.Round(back.RegionY, 5), Math.Round(back.RegionW, 5), Math.Round(back.RegionH, 5)));
    }

    [Fact]
    public void Corrupt_or_foreign_blobs_deserialize_to_null_instead_of_throwing()
    {
        var good = HeatmapMath.Serialize(new Heatmap(2, 2, new[] { 1f, 2f, 3f, 4f }));
        Assert.Null(HeatmapMath.TryDeserialize(null));
        Assert.Null(HeatmapMath.TryDeserialize(Array.Empty<byte>()));
        Assert.Null(HeatmapMath.TryDeserialize(good[..^1]));                                       // 잘림
        Assert.Null(HeatmapMath.TryDeserialize(good.Concat(new byte[] { 0 }).ToArray()));          // 길이 초과
        var badMagic = (byte[])good.Clone(); badMagic[0] = (byte)'X';
        Assert.Null(HeatmapMath.TryDeserialize(badMagic));
        var huge = (byte[])good.Clone(); BitConverter.GetBytes(100000).CopyTo(huge, 4);            // 너비 100000
        Assert.Null(HeatmapMath.TryDeserialize(huge));
        var nan = (byte[])good.Clone(); BitConverter.GetBytes(float.NaN).CopyTo(nan, 28);          // 첫 값이 NaN
        Assert.Null(HeatmapMath.TryDeserialize(nan));
    }

    [Fact]
    public void Serialize_rejects_inconsistent_sizes()
    {
        Assert.Throws<ArgumentException>(() => HeatmapMath.Serialize(new Heatmap(2, 2, new float[3])));
        Assert.Throws<ArgumentException>(() => HeatmapMath.Serialize(new Heatmap(0, 0, Array.Empty<float>())));
    }

    [Fact]
    public void WithRegion_uses_roi_only_when_present()
    {
        var h = new Heatmap(2, 2, new float[4]);
        Assert.Same(h, HeatmapMath.WithRegion(h, null));
        var r = HeatmapMath.WithRegion(h, new Roi(0.25, 0.5, 0.5, 0.25))!;
        Assert.Equal((0.25, 0.5, 0.5, 0.25), (r.RegionX, r.RegionY, r.RegionW, r.RegionH));
        Assert.Null(HeatmapMath.WithRegion(null, new Roi(0, 0, 1, 1)));
    }
}

public class HeatmapPipelineAndStorageTests
{
    private sealed class HeatEngine : IPreprocessor, IInferenceEngine
    {
        public ModelInfo Model { get; } = new("heat", "1", "classification");
        public PreprocessedImage Process(ImageFrame f, InspectionSettings s) => new(f.Path, Array.Empty<float>(), 1, 1, 1, 1);
        public InferenceOutput Run(PreprocessedImage i)
            => InferenceOutput.FromScore(i.Path.Contains("_ng") ? 0.9f : 0.1f, new Heatmap(2, 2, new[] { 0f, 1f, 2f, 3f }));
    }

    [Fact]
    public async Task Pipeline_attaches_heatmap_and_roi_region_to_results()
    {
        var repo = new MemoryRepo(); var e = new HeatEngine();
        var roi = new Roi(0.25, 0.25, 0.5, 0.5);
        await new InspectionPipeline(e, e, new ThresholdJudge(), repo)
            .RunAsync(new ListSource(new[] { "a_ng.png" }), new InspectionSettings { Roi = roi }, null, CancellationToken.None);
        var h = repo.Saved.Single().Heatmap!;
        Assert.Equal(new[] { 0f, 1f, 2f, 3f }, h.Values);
        Assert.Equal((0.25, 0.25, 0.5, 0.5), (h.RegionX, h.RegionY, h.RegionW, h.RegionH));      // ROI를 썼으면 격자가 덮는 영역도 ROI
    }

    [Fact]
    public async Task Sqlite_stores_heatmap_for_ng_only_and_reads_it_back_with_region()
    {
        using var d = new TempDir();
        var repo = new SqliteResultRepository(System.IO.Path.Combine(d.Path, "t.db"));
        var job = await repo.BeginJobAsync(new ModelInfo("m", "v", "c"), "s", 0.5f);
        var hm = new Heatmap(2, 2, new[] { 0f, 1f, 2f, 3f }, 0.1, 0.2, 0.5, 0.6);
        await repo.SaveAsync(new InspectionResult { JobId = job, ImagePath = "ng.png", Verdict = Verdict.NG, Score = 0.9f, Heatmap = hm });
        await repo.SaveAsync(new InspectionResult { JobId = job, ImagePath = "ok.png", Verdict = Verdict.OK, Score = 0.1f, Heatmap = hm });
        await repo.SaveAsync(new InspectionResult { JobId = job, ImagePath = "nohm.png", Verdict = Verdict.NG, Score = 0.9f });

        var rows = await repo.QueryAsync(new HistoryFilter());
        var ng = rows.Single(r => r.ImagePath == "ng.png").Heatmap!;
        Assert.Equal(hm.Values, ng.Values);
        Assert.Equal(0.5, Math.Round(ng.RegionW, 5));
        Assert.Null(rows.Single(r => r.ImagePath == "ok.png").Heatmap);                  // OK는 저장하지 않음
        Assert.Null(rows.Single(r => r.ImagePath == "nohm.png").Heatmap);                // 모델이 CAM을 안 줬으면 없음
    }

    [Fact]
    public async Task Invalid_heatmap_does_not_lose_the_result()
    {
        using var d = new TempDir();
        var repo = new SqliteResultRepository(System.IO.Path.Combine(d.Path, "t.db"));
        var job = await repo.BeginJobAsync(new ModelInfo("m", "v", "c"), "s", 0.5f);
        await repo.SaveAsync(new InspectionResult { JobId = job, ImagePath = "x.png", Verdict = Verdict.NG, Score = 0.9f, Heatmap = new Heatmap(2, 2, new float[3]) });   // 크기 불일치
        var r = (await repo.QueryAsync(new HistoryFilter())).Single();
        Assert.Equal(Verdict.NG, r.Verdict);
        Assert.Null(r.Heatmap);
    }

    [Fact]
    public async Task Old_database_without_heatmap_column_is_migrated_and_keeps_its_rows()
    {
        using var d = new TempDir();
        var path = System.IO.Path.Combine(d.Path, "old.db");
        using (var c = new SqliteConnection($"Data Source={path}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE inspection_job(id INTEGER PRIMARY KEY AUTOINCREMENT, model_name TEXT NOT NULL, model_version TEXT NOT NULL, source_name TEXT NOT NULL, status TEXT NOT NULL, threshold REAL NOT NULL, started_at TEXT NOT NULL, ended_at TEXT NULL);
                CREATE TABLE inspection_result(id INTEGER PRIMARY KEY AUTOINCREMENT, job_id INTEGER NOT NULL, image_path TEXT NOT NULL, verdict TEXT NOT NULL, score REAL NOT NULL, elapsed_ms INTEGER NOT NULL, error_message TEXT NULL, inspected_at TEXT NOT NULL);
                INSERT INTO inspection_job(model_name,model_version,source_name,status,threshold,started_at) VALUES('m','v','s','Completed',0.5,'2026-11-01T00:00:00Z');
                INSERT INTO inspection_result(job_id,image_path,verdict,score,elapsed_ms,inspected_at) VALUES(1,'legacy.png','NG',0.9,10,'2026-11-01T00:00:01Z');
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var repo = new SqliteResultRepository(path);                                       // 열 때 열이 추가돼야 한다
        var legacy = (await repo.QueryAsync(new HistoryFilter())).Single();
        Assert.Equal("legacy.png", legacy.ImagePath);
        Assert.Null(legacy.Heatmap);

        await repo.SaveAsync(new InspectionResult { JobId = 1, ImagePath = "new.png", Verdict = Verdict.NG, Score = 0.9f, Heatmap = new Heatmap(1, 1, new[] { 1f }) });
        Assert.NotNull((await repo.QueryAsync(new HistoryFilter())).Single(r => r.ImagePath == "new.png").Heatmap);
        _ = new SqliteResultRepository(path);                                               // 두 번째 열기도 문제 없음(열이 이미 있음)
    }
}

/// <summary>
/// cam 출력이 있는 실제 ONNX(training/add_cam_output.py로 만든 model_cam.onnx)가 있을 때만 검사한다. 환경변수가 없으면 통과(CI).
///   VISIONQC_PARITY_MODEL_CAM = out/model_cam.onnx
/// </summary>
public class OnnxCamOutputTests
{
    [Fact]
    public void Model_with_cam_output_returns_a_7x7_heatmap_and_unchanged_score()
    {
        var cam = Environment.GetEnvironmentVariable("VISIONQC_PARITY_MODEL_CAM");
        var plain = Environment.GetEnvironmentVariable("VISIONQC_PARITY_MODEL");
        var cases = Environment.GetEnvironmentVariable("VISIONQC_PARITY_CASES");
        if (string.IsNullOrEmpty(cam) || string.IsNullOrEmpty(plain) || string.IsNullOrEmpty(cases)) return;

        var settings = new InspectionSettings { InputSize = 224 };
        var pre = new VisionQC.Imaging.OpenCvPreprocessor();
        using var withCam = new OnnxClassifier(cam);
        using var without = new OnnxClassifier(plain);
        foreach (var c in cases.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var img = pre.Process(new ImageFrame(c.Split('|')[0]), settings);
            var a = withCam.Run(img); var b = without.Run(img);
            Assert.NotNull(a.Heatmap);
            Assert.Equal((7, 7), (a.Heatmap!.Width, a.Heatmap.Height));
            Assert.Null(b.Heatmap);                                                         // cam 출력이 없는 모델은 히트맵 없음
            Assert.Equal(b.Score, a.Score);                                                 // 점수는 한 비트도 안 달라진다
        }
    }
}
