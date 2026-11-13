using VisionQC.Core;
using VisionQC.Imaging;

namespace VisionQC.Tests;

public class FolderWatchTests
{
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(40);

    private static void Touch(string dir, string name, int bytes = 10)
        => File.WriteAllBytes(System.IO.Path.Combine(dir, name), new byte[bytes]);

    /// <summary>소스를 백그라운드에서 읽어 모은다. Stop()으로 취소하면 수집된 목록을 돌려준다.</summary>
    private sealed class Collector : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _task;
        public List<string> Got { get; } = new();
        public Exception? Error { get; private set; }

        public Collector(IImageSource src)
        {
            _task = Task.Run(async () =>
            {
                try { await foreach (var f in src.ReadAsync(_cts.Token)) lock (Got) Got.Add(System.IO.Path.GetFileName(f.Path)); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Error = ex; }
            });
        }

        public string[] Snapshot() { lock (Got) return Got.ToArray(); }

        public async Task<string[]> WaitForAsync(int count, int timeoutMs = 5000)
        {
            var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < end && Snapshot().Length < count && Error is null) await Task.Delay(20);
            return Snapshot();
        }

        public async Task StopAsync() { _cts.Cancel(); await _task; }
        public async ValueTask DisposeAsync() => await StopAsync();
    }

    [Fact]
    public async Task New_files_are_picked_up_and_existing_ones_ignored_by_default()
    {
        using var d = new TempDir();
        Touch(d.Path, "old.png");
        await using var c = new Collector(new FolderWatchImageSource(d.Path, pollInterval: Fast));
        await Task.Delay(200);                                  // 기준선(이미 있던 파일)을 잡을 시간
        Touch(d.Path, "new1.png");
        Touch(d.Path, "new2.jpg");
        var got = await c.WaitForAsync(2);
        Assert.Equal(new[] { "new1.png", "new2.jpg" }, got.OrderBy(x => x).ToArray());
        Assert.DoesNotContain("old.png", got);
    }

    [Fact]
    public async Task Existing_files_are_included_when_requested()
    {
        using var d = new TempDir();
        Touch(d.Path, "a.png");
        Touch(d.Path, "b.png");
        await using var c = new Collector(new FolderWatchImageSource(d.Path, includeExisting: true, pollInterval: Fast));
        var got = await c.WaitForAsync(2);
        Assert.Equal(new[] { "a.png", "b.png" }, got.OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task Non_image_files_are_ignored_and_each_file_is_emitted_once()
    {
        using var d = new TempDir();
        await using var c = new Collector(new FolderWatchImageSource(d.Path, pollInterval: Fast));
        await Task.Delay(150);
        Touch(d.Path, "note.txt");
        Touch(d.Path, "x.png");
        await c.WaitForAsync(1);
        await Task.Delay(400);                                  // 여러 번 더 폴링해도 다시 나오면 안 됨
        Assert.Equal(new[] { "x.png" }, c.Snapshot());
    }

    [Fact]
    public async Task File_still_being_written_waits_until_writer_is_done()
    {
        using var d = new TempDir();
        var path = System.IO.Path.Combine(d.Path, "slow.png");
        await using var c = new Collector(new FolderWatchImageSource(d.Path, pollInterval: Fast));
        await Task.Delay(150);

        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))   // 쓰는 중(핸들 열림)
        {
            fs.Write(new byte[100]); fs.Flush();
            await Task.Delay(400);                              // 크기가 안 변해도 핸들이 열려 있으면 내보내지 않는다
            Assert.Empty(c.Snapshot());
            fs.Write(new byte[100]);
        }                                                       // 닫힘 = 쓰기 끝
        var got = await c.WaitForAsync(1);
        Assert.Equal(new[] { "slow.png" }, got);
    }

    [Fact]
    public async Task File_that_never_stabilizes_is_emitted_after_timeout_so_it_is_not_silently_lost()
    {
        using var d = new TempDir();
        var path = System.IO.Path.Combine(d.Path, "stuck.png");
        await using var c = new Collector(new FolderWatchImageSource(d.Path, pollInterval: Fast, stableTimeout: TimeSpan.FromMilliseconds(300)));
        await Task.Delay(150);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);   // 계속 열어 둠
        fs.Write(new byte[10]); fs.Flush();
        var got = await c.WaitForAsync(1, 3000);
        Assert.Equal(new[] { "stuck.png" }, got);               // 이후 읽기 실패는 파이프라인이 ERROR로 기록
    }

    [Fact]
    public async Task Cancellation_ends_the_stream()
    {
        using var d = new TempDir();
        var c = new Collector(new FolderWatchImageSource(d.Path, pollInterval: Fast));
        await Task.Delay(100);
        var stop = c.StopAsync();
        Assert.Same(stop, await Task.WhenAny(stop, Task.Delay(3000)));
        Assert.Null(c.Error);
    }

    [Fact]
    public async Task Missing_folder_throws_at_start()
    {
        var src = new FolderWatchImageSource(@"Z:\no\such\dir");
        await Assert.ThrowsAsync<DirectoryNotFoundException>(async () =>
        {
            await foreach (var _ in src.ReadAsync(CancellationToken.None)) { }
        });
    }

    [Fact]
    public async Task Folder_removed_while_watching_becomes_a_fatal_error()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "visionqc_watch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await using var c = new Collector(new FolderWatchImageSource(dir, pollInterval: TimeSpan.FromMilliseconds(20)));
        await Task.Delay(120);
        Directory.Delete(dir);
        var end = DateTime.UtcNow.AddSeconds(5);
        while (c.Error is null && DateTime.UtcNow < end) await Task.Delay(30);
        Assert.IsAssignableFrom<IOException>(c.Error);          // 연속 10번 실패 후 오류 (DirectoryNotFoundException 포함)
    }

    [Fact]
    public async Task Pipeline_in_watch_mode_processes_new_files_and_ends_as_cancelled_when_stopped()
    {
        using var d = new TempDir();
        var repo = new MemoryRepo(); var fe = new FakeEngineAndPre();
        var pipeline = new InspectionPipeline(fe, fe, new ThresholdJudge(), repo);
        using var cts = new CancellationTokenSource();
        var run = pipeline.RunAsync(new FolderWatchImageSource(d.Path, pollInterval: Fast), new InspectionSettings(), null, cts.Token);

        await Task.Delay(150);
        Touch(d.Path, "a_ok.png");
        Touch(d.Path, "b_ng.png");
        var end = DateTime.UtcNow.AddSeconds(5);
        while (repo.Saved.Count < 2 && DateTime.UtcNow < end) await Task.Delay(30);

        cts.Cancel();
        var sum = await run;
        Assert.Equal(JobStatus.Cancelled, sum.Status);          // 감시 모드의 정상 종료 = 중지
        Assert.Equal((2, 1, 1), (sum.Total, sum.Ok, sum.Ng));
        Assert.Equal(JobStatus.Cancelled, repo.EndStatus);
    }
}
