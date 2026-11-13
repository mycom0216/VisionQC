using OpenCvSharp;
using VisionQC.Core;
using VisionQC.Data;
using VisionQC.Imaging;
using VisionQC.Inference;

namespace VisionQC.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "visionqc_" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path, true); } catch { }
    }
}

public class ImagingTests
{
    private static void WritePng(string path, int w, int h, Scalar color)
    {
        using var m = new Mat(h, w, MatType.CV_8UC3, color);
        Cv2.ImWrite(path, m);
    }

    [Fact]
    public async Task Folder_source_lists_only_images_in_name_order()
    {
        using var d = new TempDir();
        WritePng(System.IO.Path.Combine(d.Path, "b.png"), 4, 4, Scalar.White);
        WritePng(System.IO.Path.Combine(d.Path, "a.png"), 4, 4, Scalar.White);
        File.WriteAllText(System.IO.Path.Combine(d.Path, "note.txt"), "x");

        var names = new List<string>();
        await foreach (var f in new FolderImageSource(d.Path).ReadAsync(CancellationToken.None))
            names.Add(System.IO.Path.GetFileName(f.Path));
        Assert.Equal(new[] { "a.png", "b.png" }, names);
    }

    [Fact]
    public async Task Folder_source_missing_folder_throws()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(async () =>
        {
            await foreach (var _ in new FolderImageSource(@"Z:\no\such\dir").ReadAsync(CancellationToken.None)) { }
        });
    }

    [Fact]
    public void Preprocess_shape_and_channel_order_rgb_chw()
    {
        using var d = new TempDir();
        var p = System.IO.Path.Combine(d.Path, "red.png");
        WritePng(p, 40, 30, new Scalar(0, 0, 255));            // BGR: 순수 빨강
        var img = new OpenCvPreprocessor(new[] { 0f, 0f, 0f }, new[] { 1f, 1f, 1f })
            .Process(new ImageFrame(p), new InspectionSettings { InputSize = 8 });

        Assert.Equal((8, 8, 40, 30), (img.Width, img.Height, img.OriginalWidth, img.OriginalHeight));
        Assert.Equal(3 * 64, img.Data.Length);
        Assert.Equal(1f, img.Data[0], 3);                      // R 평면
        Assert.Equal(0f, img.Data[64], 3);                     // G 평면
        Assert.Equal(0f, img.Data[128], 3);                    // B 평면
    }

    [Fact]
    public void Preprocess_corrupt_file_throws_clear_error()
    {
        using var d = new TempDir();
        var p = System.IO.Path.Combine(d.Path, "broken.png");
        File.WriteAllBytes(p, new byte[] { 1, 2, 3, 4 });
        var ex = Assert.Throws<InvalidDataException>(() => new OpenCvPreprocessor().Process(new ImageFrame(p), new InspectionSettings()));
        Assert.Contains("broken.png", ex.Message);
    }

    [Fact]
    public void Preprocess_works_with_korean_path()
    {
        using var d = new TempDir();
        var p = System.IO.Path.Combine(d.Path, "한글_이미지.png");
        // ImWrite는 한글 경로에서 실패할 수 있어 바이트로 직접 저장
        using var m = new Mat(10, 10, MatType.CV_8UC3, Scalar.White);
        Cv2.ImEncode(".png", m, out var buf);
        File.WriteAllBytes(p, buf);
        var img = new OpenCvPreprocessor().Process(new ImageFrame(p), new InspectionSettings { InputSize = 4 });
        Assert.Equal(4, img.Width);
    }
}

public class OnnxMathTests
{
    [Fact]
    public void Softmax_two_logits_returns_defect_probability()
        => Assert.Equal(0.5f, OnnxClassifier.ToDefectProbability(new[] { 2f, 2f }), 5);

    [Fact]
    public void Softmax_is_stable_for_large_logits()
        => Assert.Equal(1f, OnnxClassifier.ToDefectProbability(new[] { 0f, 1000f }), 5);

    [Fact]
    public void Single_logit_uses_sigmoid()
        => Assert.Equal(0.5f, OnnxClassifier.ToDefectProbability(new[] { 0f }), 5);

    [Fact]
    public void Empty_output_is_nan_so_judge_yields_error()
        => Assert.True(float.IsNaN(OnnxClassifier.ToDefectProbability(Array.Empty<float>())));

    [Fact]
    public void Missing_model_file_throws_file_not_found()
        => Assert.Throws<FileNotFoundException>(() => new OnnxClassifier(@"Z:\nope.onnx"));
}

public class RepositoryTests
{
    [Fact]
    public async Task Roundtrip_with_defects_and_filter()
    {
        using var d = new TempDir();
        var repo = new SqliteResultRepository(System.IO.Path.Combine(d.Path, "t.db"));
        var jobId = await repo.BeginJobAsync(new ModelInfo("m.onnx", "abc", "classification"), "folder:x", 0.5f);

        await repo.SaveAsync(new InspectionResult { JobId = jobId, ImagePath = "a.png", Verdict = Verdict.OK, Score = 0.1f, ElapsedMs = 5 });
        await repo.SaveAsync(new InspectionResult
        {
            JobId = jobId, ImagePath = "b.png", Verdict = Verdict.NG, Score = 0.9f, ElapsedMs = 7,
            Defects = new[] { new Detection("scratch", 0.9f, 1, 2, 3, 4) }
        });
        await repo.SaveAsync(new InspectionResult { JobId = jobId, ImagePath = "c.png", Verdict = Verdict.ERROR, Score = float.NaN, ErrorMessage = "corrupt" });
        await repo.EndJobAsync(jobId, JobStatus.Completed);

        var all = await repo.QueryAsync(new HistoryFilter());
        Assert.Equal(3, all.Count);

        var ng = (await repo.QueryAsync(new HistoryFilter(Verdict: Verdict.NG))).Single();
        Assert.Equal("b.png", ng.ImagePath);
        Assert.Equal(new Detection("scratch", 0.9f, 1, 2, 3, 4), ng.Defects.Single());

        var err = (await repo.QueryAsync(new HistoryFilter(Verdict: Verdict.ERROR))).Single();
        Assert.Equal("corrupt", err.ErrorMessage);
    }

    [Fact]
    public async Task Time_filter_and_limit()
    {
        using var d = new TempDir();
        var repo = new SqliteResultRepository(System.IO.Path.Combine(d.Path, "t.db"));
        var job = await repo.BeginJobAsync(new ModelInfo("m", "v", "c"), "s", 0.5f);
        var now = DateTime.UtcNow;
        for (int i = 0; i < 5; i++)
            await repo.SaveAsync(new InspectionResult { JobId = job, ImagePath = $"{i}.png", Verdict = Verdict.OK, InspectedAtUtc = now.AddMinutes(-i) });

        var recent = await repo.QueryAsync(new HistoryFilter(FromUtc: now.AddMinutes(-2.5)));
        Assert.Equal(3, recent.Count);
        Assert.Equal("0.png", recent[0].ImagePath);            // 최신순

        Assert.Equal(2, (await repo.QueryAsync(new HistoryFilter(Limit: 2))).Count);
    }

    [Fact]
    public async Task Pipeline_end_to_end_with_real_folder_preprocessor_and_sqlite()
    {
        using var d = new TempDir();
        var imgs = System.IO.Path.Combine(d.Path, "imgs");
        Directory.CreateDirectory(imgs);
        foreach (var n in new[] { "a_ok.png", "b_ng.png" })
        {
            using var m = new Mat(20, 20, MatType.CV_8UC3, Scalar.Gray);
            Cv2.ImWrite(System.IO.Path.Combine(imgs, n), m);
        }
        File.WriteAllBytes(System.IO.Path.Combine(imgs, "c_bad.png"), new byte[] { 9, 9, 9 });

        var repo = new SqliteResultRepository(System.IO.Path.Combine(d.Path, "t.db"));
        var fe = new FakeEngineAndPre();
        var pipeline = new InspectionPipeline(new OpenCvPreprocessor(), fe, new ThresholdJudge(), repo);
        var sum = await pipeline.RunAsync(new FolderImageSource(imgs), new InspectionSettings { InputSize = 8 }, null, CancellationToken.None);

        Assert.Equal((3, 1, 1, 1), (sum.Total, sum.Ok, sum.Ng, sum.Error));
        var rows = await repo.QueryAsync(new HistoryFilter());
        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, r => r.ImagePath.EndsWith("c_bad.png") && r.Verdict == Verdict.ERROR);
    }
}
