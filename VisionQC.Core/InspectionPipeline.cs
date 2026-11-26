// ============================================================================
// InspectionPipeline.cs - 검사 전체를 조율하는 파이프라인(이 프로젝트의 중심)
//
// 흐름: 이미지 공급원 -> [제한 큐] -> 전처리 -> ONNX 추론 -> 임계값 판정 -> 결과 저장소 -> 화면(진행 보고)
// 설계 이유:
//  - 생산자-소비자 구조: 이미지 읽기(생산자)와 추론(소비자)을 분리해 느린 쪽 때문에 전체가 멈추지 않게 한다.
//  - 제한 큐(Bounded Channel): 읽기가 훨씬 빠를 때 메모리에 이미지가 무한정 쌓이는 것을 막는다(가득 차면 읽기가 기다림).
//  - 이미지 1장의 실패는 전체를 멈추지 않고 ERROR 판정으로 기록한다(현장에서는 한 장 때문에 라인이 서면 안 됨).
//  - 취소/저장 실패에도 요약(RunSummary)과 Job 종료 기록은 반드시 남긴다.
// ============================================================================
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

    /// <summary>각 단계 구현을 주입받는다(의존성 주입). 인터페이스만 알기 때문에 테스트에서 가짜 구현으로 바꿔 끼울 수 있다.</summary>
    public InspectionPipeline(IPreprocessor pre, IInferenceEngine engine, IJudge judge, IResultRepository repo)
    {
        _pre = pre; _engine = engine; _judge = judge; _repo = repo;
    }

    /// <summary>
    /// 검사 1회를 끝까지 실행한다.
    /// </summary>
    /// <param name="source">이미지 공급원(폴더 등).</param>
    /// <param name="settings">임계값·ROI·큐 용량·워커 수 등 실행 설정.</param>
    /// <param name="progress">결과 1장이 나올 때마다 호출되는 진행 보고(화면 갱신용). 필요 없으면 null.</param>
    /// <param name="ct">취소 토큰. 사용자가 "중지"를 누르면 신호가 온다.</param>
    /// <returns>처리 건수·판정별 건수·저장 실패 수·치명적 오류를 담은 요약.</returns>
    public async Task<RunSummary> RunAsync(IImageSource source, InspectionSettings settings,
        IProgress<InspectionResult>? progress, CancellationToken ct)
    {
        // 작업 시작 기록. 취소 토큰으로 CancellationToken.None 을 쓰는 이유: 사용자가 이미 취소했더라도
        // 이력에는 작업이 남아야 하고(그래야 종료 상태도 기록할 수 있음), 기록 자체는 짧은 일이기 때문이다.
        var jobId = await _repo.BeginJobAsync(_engine.Model, source.Name, settings.Threshold, CancellationToken.None)
            .ConfigureAwait(false);

        // 용량이 제한된 큐. 용량은 최소 1로 보정한다(0 이하 설정으로 교착되는 것을 방지).
        // SingleWriter: 쓰는 쪽(생산자)이 하나뿐이라 내부 잠금을 줄여 준다. FullMode.Wait: 가득 차면 쓰는 쪽이 기다린다.
        var channel = Channel.CreateBounded<ImageFrame>(new BoundedChannelOptions(Math.Max(1, settings.QueueCapacity))
        {
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        // 집계 카운터. 여러 워커가 동시에 갱신하므로 아래에서 Interlocked(원자적 증가)를 쓴다.
        string? fatal = null;
        int total = 0, ok = 0, ng = 0, error = 0, saveFailures = 0;

        // 생산자: 이미지 공급원에서 한 장씩 읽어 큐에 넣는다. 취소는 정상 종료로 보고,
        // 그 밖의 예외(폴더 접근 불가 등)는 치명적 오류(fatal)로 기록한다. 어떤 경우든 마지막에 큐를 "완료"로 닫아 소비자가 끝나게 한다.
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

        // 소비자(워커): 큐에서 꺼내 전처리·추론·판정 후 저장하고 진행을 알린다. 워커 수만큼 병렬로 돈다.
        async Task Worker()
        {
            try
            {
                while (await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    while (channel.Reader.TryRead(out var frame))
                    {
                        // 한 장 처리(예외는 Process 안에서 ERROR 결과로 바뀌므로 여기서는 던지지 않는다).
                        var result = Process(frame, settings, jobId);
                        Interlocked.Increment(ref total);
                        switch (result.Verdict)
                        {
                            case Verdict.OK: Interlocked.Increment(ref ok); break;
                            case Verdict.NG: Interlocked.Increment(ref ng); break;
                            default: Interlocked.Increment(ref error); break;
                        }

                        // 저장 실패는 검사를 멈추지 않는다. 건수만 세어 요약에서 알리고 다음 이미지로 계속 진행한다.
                        try { await _repo.SaveAsync(result, CancellationToken.None).ConfigureAwait(false); }
                        catch { Interlocked.Increment(ref saveFailures); }

                        progress?.Report(result);
                        // 취소되면 방금 처리한 1장까지만 저장·보고하고 빠져나간다(큐에 남은 이미지는 버림).
                        if (ct.IsCancellationRequested) return;
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        // 워커 수만큼 소비자를 띄운다(최소 1). 모두 끝날 때까지 기다린다.
        var workers = Enumerable.Range(0, Math.Max(1, settings.Workers)).Select(_ => Task.Run(Worker)).ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);
        // 워커가 먼저 취소로 빠져나가면 생산자가 가득 찬 큐에서 막힐 수 있다 → 완료 처리 후 기다림
        channel.Writer.TryComplete();
        await producer.ConfigureAwait(false);

        // 종료 상태 결정: 공급원이 치명적으로 실패했으면 Failed, 사용자가 취소했으면 Cancelled, 그 외 Completed.
        var status = fatal != null ? JobStatus.Failed
                   : ct.IsCancellationRequested ? JobStatus.Cancelled
                   : JobStatus.Completed;
        // 종료 기록 실패도 저장 실패 건수에 포함해 사용자에게 알린다.
        try { await _repo.EndJobAsync(jobId, status, CancellationToken.None).ConfigureAwait(false); }
        catch { Interlocked.Increment(ref saveFailures); }

        return new RunSummary(jobId, status, total, ok, ng, error, saveFailures, fatal);
    }

    /// <summary>
    /// 이미지 1장을 전처리 -> 추론 -> 판정하여 결과 객체로 만든다. 어느 단계에서든 예외가 나면(손상된 이미지 등)
    /// 던지지 않고 ERROR 판정 + 오류 메시지를 담은 결과를 돌려준다. 처리 시간은 Stopwatch 로 잰다.
    /// </summary>
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
                ElapsedMs = (int)sw.ElapsedMilliseconds, ErrorMessage = j.Reason, Defects = j.Defects,
                Heatmap = HeatmapMath.WithRegion(output.Heatmap, settings.Roi)     // ROI를 썼다면 히트맵이 덮는 영역도 ROI
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
