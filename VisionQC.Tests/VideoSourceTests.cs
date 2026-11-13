using OpenCvSharp;
using VisionQC.Core;
using VisionQC.Imaging;

namespace VisionQC.Tests;

public class VideoSourceTests
{
    /// <summary>테스트용 영상 파일 생성(MJPG AVI). 프레임마다 밝기가 달라 저장된 PNG가 서로 다른지 확인할 수 있다.</summary>
    private static string MakeVideo(string dir, int frames, double fps = 10)
    {
        var path = System.IO.Path.Combine(dir, "clip.avi");
        using var w = new VideoWriter(path, FourCC.MJPG, fps, new Size(64, 48));
        Assert.True(w.IsOpened(), "이 환경에서 영상 파일을 만들 수 없음(코덱)");
        for (int i = 0; i < frames; i++)
        {
            using var m = new Mat(48, 64, MatType.CV_8UC3, new Scalar(i * 6, i * 6, i * 6));
            w.Write(m);
        }
        return path;
    }

    private static async Task<List<string>> ReadAll(IImageSource src, CancellationToken ct = default)
    {
        var list = new List<string>();
        await foreach (var f in src.ReadAsync(ct)) list.Add(f.Path);
        return list;
    }

    [Fact]
    public async Task Video_file_is_sampled_by_interval_saved_as_png_and_ends_at_file_end()
    {
        using var d = new TempDir();
        var video = MakeVideo(d.Path, frames: 30, fps: 10);
        var outDir = System.IO.Path.Combine(d.Path, "caps");

        // 10fps에서 간격 1초 = 10프레임마다 → 30프레임에서 0, 10, 20번째 = 3장
        var files = await ReadAll(new VideoCaptureImageSource(video, outDir, TimeSpan.FromSeconds(1)));
        Assert.Equal(3, files.Count);
        Assert.All(files, f => Assert.StartsWith(outDir, f));
        Assert.Equal(3, files.Distinct().Count());                                        // 이름이 겹치지 않는다
        Assert.Equal(files.OrderBy(x => x).ToList(), files);                              // 이름순 = 촬영순

        // 저장된 PNG가 실제로 읽히고, 프레임이 달라 밝기도 다르다
        var means = files.Select(f => { using var m = Cv2.ImDecode(File.ReadAllBytes(f), ImreadModes.Color); return Cv2.Mean(m).Val0; }).ToList();
        Assert.True(means[0] < means[1] && means[1] < means[2], $"밝기 증가 순서가 아님: {string.Join(", ", means)}");
    }

    [Fact]
    public async Task Short_interval_keeps_every_frame_and_creates_output_dir()
    {
        using var d = new TempDir();
        var video = MakeVideo(d.Path, frames: 12, fps: 10);
        var outDir = System.IO.Path.Combine(d.Path, "new", "caps");                       // 없는 폴더
        var files = await ReadAll(new VideoCaptureImageSource(video, outDir, TimeSpan.FromMilliseconds(100)));
        Assert.Equal(12, files.Count);
        Assert.True(Directory.Exists(outDir));
    }

    [Fact]
    public async Task Cancellation_stops_midway()
    {
        using var d = new TempDir();
        var video = MakeVideo(d.Path, frames: 40, fps: 10);
        using var cts = new CancellationTokenSource();
        var got = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var f in new VideoCaptureImageSource(video, System.IO.Path.Combine(d.Path, "c"), TimeSpan.FromMilliseconds(100)).ReadAsync(cts.Token))
            {
                got.Add(f.Path);
                if (got.Count == 3) cts.Cancel();
            }
        });
        Assert.InRange(got.Count, 3, 4);
    }

    [Fact]
    public async Task Missing_or_unreadable_video_and_missing_camera_fail_clearly()
    {
        using var d = new TempDir();
        await Assert.ThrowsAsync<FileNotFoundException>(() => ReadAll(new VideoCaptureImageSource(System.IO.Path.Combine(d.Path, "none.avi"), d.Path, TimeSpan.FromSeconds(1))));

        var junk = System.IO.Path.Combine(d.Path, "junk.avi");
        File.WriteAllBytes(junk, new byte[2048]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAll(new VideoCaptureImageSource(junk, d.Path, TimeSpan.FromSeconds(1))));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAll(new VideoCaptureImageSource("99", d.Path, TimeSpan.FromSeconds(1))));   // 없는 카메라 번호
        Assert.Contains("카메라 99", ex.Message);
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("2", true)]
    [InlineData(" 1 ", true)]
    [InlineData(@"C:\clips\line1.avi", false)]
    [InlineData("-1", false)]                                                              // 음수는 번호가 아니라 파일 경로로 취급
    public void Spec_distinguishes_camera_index_from_file(string spec, bool camera)
        => Assert.Equal(camera, new VideoCaptureImageSource(spec, "x", TimeSpan.FromSeconds(1)).IsCamera);

    [Fact]
    public void Constructor_validates_arguments()
    {
        Assert.Throws<ArgumentException>(() => new VideoCaptureImageSource("  ", "x", TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VideoCaptureImageSource("0", "x", TimeSpan.FromMilliseconds(10)));
    }

    [Fact]
    public async Task Pipeline_can_inspect_frames_captured_from_a_video()
    {
        using var d = new TempDir();
        var video = MakeVideo(d.Path, frames: 20, fps: 10);
        var repo = new MemoryRepo(); var fe = new FakeEngineAndPre();
        var pipeline = new InspectionPipeline(new OpenCvPreprocessor(), fe, new ThresholdJudge(), repo);
        var sum = await pipeline.RunAsync(new VideoCaptureImageSource(video, System.IO.Path.Combine(d.Path, "c"), TimeSpan.FromMilliseconds(500)),
            new InspectionSettings { InputSize = 8 }, null, CancellationToken.None);
        Assert.Equal(JobStatus.Completed, sum.Status);                                     // 영상은 끝나면 정상 종료
        Assert.Equal(4, sum.Total);                                                        // 20프레임 / 5프레임 간격
        Assert.Equal(0, sum.Error);
        Assert.All(repo.Saved, r => Assert.StartsWith(System.IO.Path.Combine(d.Path, "c"), r.ImagePath));   // 저장된 이력은 캡처 PNG 경로를 가리킨다
    }
}
