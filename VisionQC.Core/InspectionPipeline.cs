using System.Diagnostics;
using System.Threading.Channels;

namespace VisionQC.Core;

/// <summary>
/// 이미지 소스 → (큐) → 전처리·추론·판정 → 저장. 생산자-소비자 구조.
/// - 이미지 1장 실패는 ERROR로 기록하고 계속한다.
/// - 취소하면 큐에 남은 이미지는 버리고, 이미 처리 중인 1장은 끝까지 처리·저장한다.
/// - 저장 실패는 세어서 요약에 담고 검사는 계속한다.
/// </summary>
public sealed class InspectionPipeline
{
    private readonly IPreprocessor _pre;
    private readonly IInferenceEngine _engine;
    private readonly IJudge _judge;
    private readonly IResultRepository _repo;

    public InspectionPipeline(IPreprocessor pre, IInferenceEngine engine, IJudge judge, IResultRepository repo)
    {
        _pre = pre; _engine = engine; _judge = judge; _repo = repo;
    }

    public async Task<RunSummary> RunAsync(IImageSource source, InspectionSettings settings,
        IProgress<InspectionResult>? progress, CancellationToken ct)
    {
        var jobId = await _repo.BeginJobAsync(_engine.Model, source.Name, settings.Threshold, CancellationToken.None)
            .ConfigureAwait(false);

        var channel = Channel.CreateBounded<ImageFrame>(new BoundedChannelOptions(Math.Max(1, settings.QueueCapacity))
        {
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        string? fatal = null;
        int total = 0, ok = 0, ng = 0, error = 0, saveFailures = 0;

        var producer = Task.Run(async () =>
        {
            try
            {
                await foreach (var frame in source.ReadAsync(ct).ConfigureAwait(false))
                    await channel.Writer.WriteAsync(frame, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { fatal = ex.Message; }
            finally { channel.Writer.TryComplete(); }
        });

        async Task Worker()
        {
            try
            {
                while (await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    while (channel.Reader.TryRead(out var frame))
                    {
                        var result = Process(frame, settings, jobId);
                        Interlocked.Increment(ref total);
                        switch (result.Verdict)
                        {
                            case Verdict.OK: Interlocked.Increment(ref ok); break;
                            case Verdict.NG: Interlocked.Increment(ref ng); break;
                            default: Interlocked.Increment(ref error); break;
                        }

                        try { await _repo.SaveAsync(result, CancellationToken.None).ConfigureAwait(false); }
                        catch { Interlocked.Increment(ref saveFailures); }

                        progress?.Report(result);
                        if (ct.IsCancellationRequested) return;
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        var workers = Enumerable.Range(0, Math.Max(1, settings.Workers)).Select(_ => Task.Run(Worker)).ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);
        // 워커가 먼저 취소로 빠져나가면 생산자가 가득 찬 큐에서 막힐 수 있다 → 완료 처리 후 기다림
        channel.Writer.TryComplete();
        await producer.ConfigureAwait(false);

        var status = fatal != null ? JobStatus.Failed
                   : ct.IsCancellationRequested ? JobStatus.Cancelled
                   : JobStatus.Completed;
        try { await _repo.EndJobAsync(jobId, status, CancellationToken.None).ConfigureAwait(false); }
        catch { Interlocked.Increment(ref saveFailures); }

        return new RunSummary(jobId, status, total, ok, ng, error, saveFailures, fatal);
    }

    private InspectionResult Process(ImageFrame frame, InspectionSettings settings, long jobId)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var image = _pre.Process(frame, settings);
            var output = _engine.Run(image);
            var j = _judge.Decide(output, settings);
            return new InspectionResult
            {
                JobId = jobId, ImagePath = frame.Path, Verdict = j.Verdict, Score = j.Score,
                ElapsedMs = (int)sw.ElapsedMilliseconds, ErrorMessage = j.Reason, Defects = j.Defects
            };
        }
        catch (Exception ex)
        {
            return new InspectionResult
            {
                JobId = jobId, ImagePath = frame.Path, Verdict = Verdict.ERROR,
                ElapsedMs = (int)sw.ElapsedMilliseconds, ErrorMessage = ex.Message
            };
        }
    }
}
