using System.Runtime.CompilerServices;
using VisionQC.Core;

namespace VisionQC.Tests;

internal sealed class ListSource : IImageSource
{
    private readonly string[] _paths; private readonly int _delayMs;
    public ListSource(IEnumerable<string> paths, int delayMs = 0) { _paths = paths.ToArray(); _delayMs = delayMs; }
    public string Name => "list";
    public async IAsyncEnumerable<ImageFrame> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var p in _paths)
        {
            ct.ThrowIfCancellationRequested();
            if (_delayMs > 0) await Task.Delay(_delayMs, ct);
            yield return new ImageFrame(p);
        }
    }
}

internal sealed class ThrowingSource : IImageSource
{
    public string Name => "throwing";
    public async IAsyncEnumerable<ImageFrame> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        if (Name.Length > 0) throw new DirectoryNotFoundException("no folder");
        yield break;
    }
}

/// <summary>파일 이름에 "bad"가 있으면 읽기 실패, "_ng"가 있으면 점수 0.9, 아니면 0.1 — 모델 없이 파이프라인만 검증.</summary>
internal sealed class FakeEngineAndPre : IPreprocessor, IInferenceEngine
{
    public ModelInfo Model { get; } = new("fake", "0", "classification");
    public int Delay { get; init; }
    public PreprocessedImage Process(ImageFrame f, InspectionSettings s)
    {
        if (f.Path.Contains("bad")) throw new InvalidDataException("corrupt");
        return new PreprocessedImage(f.Path, Array.Empty<float>(), 1, 1, 1, 1);
    }
    public InferenceOutput Run(PreprocessedImage i)
    {
        if (Delay > 0) Thread.Sleep(Delay);
        return InferenceOutput.FromScore(i.Path.Contains("_ng") ? 0.9f : 0.1f);
    }
}

internal sealed class MemoryRepo : IResultRepository
{
    public List<InspectionResult> Saved { get; } = new();
    public JobStatus? EndStatus; public bool FailSave;
    private readonly object _l = new();
    public Task<long> BeginJobAsync(ModelInfo m, string s, float t, CancellationToken ct = default) => Task.FromResult(1L);
    public Task EndJobAsync(long id, JobStatus st, CancellationToken ct = default) { EndStatus = st; return Task.CompletedTask; }
    public Task SaveAsync(InspectionResult r, CancellationToken ct = default)
    {
        if (FailSave) throw new IOException("disk full");
        lock (_l) Saved.Add(r);
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<InspectionResult>> QueryAsync(HistoryFilter f, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<InspectionResult>>(Saved);
}

internal sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _a;
    public SyncProgress(Action<T> a) => _a = a;
    public void Report(T v) => _a(v);
}

public class PipelineTests
{
    private static InspectionPipeline Make(FakeEngineAndPre fe, MemoryRepo repo) => new(fe, fe, new ThresholdJudge(), repo);

    [Fact]
    public async Task Counts_ok_ng_and_error_and_continues_after_corrupt_image()
    {
        var repo = new MemoryRepo(); var fe = new FakeEngineAndPre();
        var src = new ListSource(new[] { "a_ok.png", "b_bad.png", "c_ng.png", "d_ok.png" });
        var sum = await Make(fe, repo).RunAsync(src, new InspectionSettings(), null, CancellationToken.None);

        Assert.Equal(JobStatus.Completed, sum.Status);
        Assert.Equal((4, 2, 1, 1), (sum.Total, sum.Ok, sum.Ng, sum.Error));
        Assert.Equal(4, repo.Saved.Count);
        var bad = repo.Saved.Single(r => r.ImagePath.Contains("bad"));
        Assert.Equal(Verdict.ERROR, bad.Verdict);          // 손상 이미지를 양품으로 처리하지 않는다
        Assert.Equal("corrupt", bad.ErrorMessage);
        Assert.Equal(JobStatus.Completed, repo.EndStatus);
    }

    [Fact]
    public async Task Empty_source_completes_with_zero()
    {
        var repo = new MemoryRepo();
        var sum = await Make(new FakeEngineAndPre(), repo)
            .RunAsync(new ListSource(Array.Empty<string>()), new InspectionSettings(), null, CancellationToken.None);
        Assert.Equal(JobStatus.Completed, sum.Status);
        Assert.Equal(0, sum.Total);
    }

    [Fact]
    public async Task Source_failure_marks_job_failed()
    {
        var repo = new MemoryRepo();
        var sum = await Make(new FakeEngineAndPre(), repo)
            .RunAsync(new ThrowingSource(), new InspectionSettings(), null, CancellationToken.None);
        Assert.Equal(JobStatus.Failed, sum.Status);
        Assert.Equal("no folder", sum.FatalError);
        Assert.Equal(JobStatus.Failed, repo.EndStatus);
    }

    [Fact]
    public async Task Save_failure_is_counted_and_run_continues()
    {
        var repo = new MemoryRepo { FailSave = true };
        var sum = await Make(new FakeEngineAndPre(), repo)
            .RunAsync(new ListSource(new[] { "a.png", "b.png" }), new InspectionSettings(), null, CancellationToken.None);
        Assert.Equal(2, sum.Total);
        Assert.Equal(2, sum.SaveFailures);
        Assert.Equal(JobStatus.Completed, sum.Status);
    }

    [Fact]
    public async Task Cancel_stops_early_and_saves_what_was_processed()
    {
        var repo = new MemoryRepo(); var fe = new FakeEngineAndPre { Delay = 20 };
        var files = Enumerable.Range(0, 200).Select(i => $"img{i}.png").ToArray();
        using var cts = new CancellationTokenSource();
        int seen = 0;
        var progress = new SyncProgress<InspectionResult>(_ => { if (Interlocked.Increment(ref seen) == 3) cts.Cancel(); });

        var sum = await Make(fe, repo).RunAsync(new ListSource(files), new InspectionSettings(), progress, cts.Token);

        Assert.Equal(JobStatus.Cancelled, sum.Status);
        Assert.InRange(sum.Total, 3, 20);                  // 200장을 다 돌지 않는다
        Assert.Equal(sum.Total, repo.Saved.Count);         // 처리한 것은 모두 저장
        Assert.Equal(JobStatus.Cancelled, repo.EndStatus);
    }

    [Fact]
    public async Task Multiple_workers_process_every_image_exactly_once()
    {
        var repo = new MemoryRepo();
        var files = Enumerable.Range(0, 60).Select(i => $"img{i}_ng.png").ToArray();
        var sum = await Make(new FakeEngineAndPre { Delay = 2 }, repo)
            .RunAsync(new ListSource(files), new InspectionSettings { Workers = 4, QueueCapacity = 2 }, null, CancellationToken.None);
        Assert.Equal(60, sum.Total);
        Assert.Equal(60, repo.Saved.Select(r => r.ImagePath).Distinct().Count());
    }
}
