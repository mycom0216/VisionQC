using System.Runtime.CompilerServices;
using OpenCvSharp;
using VisionQC.Core;

namespace VisionQC.Imaging;

/// <summary>
/// 카메라(번호) 또는 영상 파일에서 프레임을 뽑아 PNG로 저장하고 그 경로를 공급한다(OpenCV VideoCapture).
/// 파이프라인·이력은 "이미지 파일 경로" 기반이라, 프레임을 저장해 두면 재판정·이력 화면에서도 같은 이미지를 다시 볼 수 있다.
///
/// · 영상 파일: <see cref="Interval"/>을 "프레임 간격"으로 환산(fps × 간격)해 N번째 프레임마다 저장하고, 파일 끝에서 정상 종료한다. 가능한 빨리 처리한다.
/// · 카메라: 실시간 간격으로 촬영한다. 기다리는 동안 프레임을 계속 버려(Grab) 오래된 버퍼 프레임이 아니라 최신 프레임을 쓴다. 중지할 때까지 계속.
///   프레임을 연속 5초 못 읽으면 연결 문제로 보고 오류로 끝낸다(조용히 멈춰 있지 않게).
///
/// 검증 범위: 영상 파일 경로는 테스트로 검증했다. <b>실제 카메라(UVC·산업용)는 장비가 없어 검증하지 못했다</b> — 카메라 전용 분기(시간 기반 촬영·버퍼 비우기·재시도)는 같은 API를 쓰지만 실장비 시험이 필요하다.
/// 저장 폴더는 계속 커지므로 오래된 캡처 정리는 운영 절차로 해야 한다.
/// </summary>
public sealed class VideoCaptureImageSource : IImageSource
{
    private readonly string _spec;
    private readonly string _outputDir;
    private readonly int? _cameraIndex;
    private readonly Func<DateTime> _now;

    public TimeSpan Interval { get; }
    public TimeSpan ReadTimeout { get; }

    /// <param name="spec">카메라 번호("0", "1" …) 또는 영상 파일 경로</param>
    /// <param name="outputDir">캡처 이미지를 저장할 폴더(없으면 만든다)</param>
    /// <param name="interval">촬영 간격(최소 50ms)</param>
    public VideoCaptureImageSource(string spec, string outputDir, TimeSpan interval, TimeSpan? readTimeout = null, Func<DateTime>? now = null)
    {
        if (string.IsNullOrWhiteSpace(spec)) throw new ArgumentException("카메라 번호 또는 영상 파일을 지정하세요.", nameof(spec));
        if (interval < TimeSpan.FromMilliseconds(50)) throw new ArgumentOutOfRangeException(nameof(interval), "촬영 간격은 50ms 이상이어야 합니다.");
        _spec = spec.Trim();
        _outputDir = outputDir;
        _cameraIndex = int.TryParse(_spec, out var idx) && idx >= 0 ? idx : null;
        Interval = interval;
        ReadTimeout = readTimeout ?? TimeSpan.FromSeconds(5);
        _now = now ?? (() => DateTime.Now);
    }

    public bool IsCamera => _cameraIndex is not null;
    public string Name => IsCamera ? $"camera:{_cameraIndex}" : $"video:{_spec}";

    public async IAsyncEnumerable<ImageFrame> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (!IsCamera && !File.Exists(_spec)) throw new FileNotFoundException($"영상 파일을 찾을 수 없음: {_spec}", _spec);

        using var cap = IsCamera ? new VideoCapture(_cameraIndex!.Value) : new VideoCapture(_spec);
        if (!cap.IsOpened()) throw new InvalidOperationException(IsCamera
            ? $"카메라 {_cameraIndex}을(를) 열 수 없습니다(연결·다른 프로그램의 사용 여부 확인)."
            : $"영상 파일을 열 수 없습니다(지원하지 않는 형식?): {_spec}");

        Directory.CreateDirectory(_outputDir);
        using var mat = new Mat();
        int n = 0;

        if (!IsCamera)
        {
            double fps = cap.Fps;
            int stride = Math.Max(1, (int)Math.Round((fps > 0 && double.IsFinite(fps) ? fps : 30) * Interval.TotalSeconds));
            for (long frameNo = 0; ; frameNo++)
            {
                ct.ThrowIfCancellationRequested();
                if (!cap.Read(mat) || mat.Empty()) yield break;                       // 파일 끝
                if (frameNo % stride != 0) continue;
                yield return Save(mat, ++n);
                await Task.Yield();
            }
        }
        else
        {
            var nextShot = _now();
            var lastGood = _now();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                // 다음 촬영 시각까지 최신 프레임을 계속 받아 버퍼가 낡지 않게 한다
                if (_now() < nextShot)
                {
                    cap.Grab();
                    await Task.Delay(10, ct).ConfigureAwait(false);
                    continue;
                }
                if (cap.Read(mat) && !mat.Empty())
                {
                    lastGood = _now();
                    nextShot = lastGood + Interval;
                    yield return Save(mat, ++n);
                }
                else
                {
                    if (_now() - lastGood > ReadTimeout)
                        throw new IOException($"카메라 {_cameraIndex}에서 {ReadTimeout.TotalSeconds:F0}초 넘게 프레임을 읽지 못했습니다(연결 끊김?).");
                    await Task.Delay(50, ct).ConfigureAwait(false);
                }
            }
        }
    }

    private ImageFrame Save(Mat frame, int n)
    {
        var name = $"capture_{_now():yyyyMMdd_HHmmss_fff}_{n:D5}.png";
        var path = Path.Combine(_outputDir, name);
        if (!Cv2.ImEncode(".png", frame, out var buf)) throw new IOException("프레임을 PNG로 인코딩하지 못했습니다.");
        File.WriteAllBytes(path, buf);                                                // 한글 경로에서도 안전하게 바이트로 저장
        return new ImageFrame(path);
    }
}
