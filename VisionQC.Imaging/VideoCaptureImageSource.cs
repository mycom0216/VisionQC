// ============================================================================
// VideoCaptureImageSource.cs
// 역할: 카메라 또는 영상 파일에서 일정 간격으로 프레임을 뽑아 PNG 파일로 저장하고, 그 경로를 이미지 소스로 공급한다.
// 파이프라인에서의 위치:
//   [이미지 소스: 여기] -> 전처리 -> 추론 -> 판정(OK/NG/ERROR) -> 이력 저장소 / UI
// 프레임을 "파일로 저장"하는 이유: 파이프라인·이력이 이미지 파일 경로를 기준으로 동작하므로, 저장해 두면 나중에 이력 화면에서
//   같은 이미지를 다시 보거나 재판정할 수 있다.
// ============================================================================
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
    private readonly string _spec;          // 사용자가 입력한 원본 지정값(카메라 번호 또는 파일 경로)
    private readonly string _outputDir;
    private readonly int? _cameraIndex;     // 숫자로 해석되면 카메라 번호, 아니면 null(= 영상 파일)
    private readonly Func<DateTime> _now;   // 현재 시각 공급자(테스트에서 가짜 시계를 주입할 수 있게 분리)

    /// <summary>프레임을 저장하는 간격. 영상 파일에서는 "N번째 프레임마다"로, 카메라에서는 실시간 간격으로 쓰인다.</summary>
    public TimeSpan Interval { get; }
    /// <summary>카메라에서 이 시간 넘게 프레임을 못 읽으면 연결 문제로 보고 오류로 끝낸다(기본 5초).</summary>
    public TimeSpan ReadTimeout { get; }

    /// <param name="readTimeout">프레임 읽기 실패 한도(기본 5초)</param>
    /// <param name="now">현재 시각 공급자(테스트용, 기본 DateTime.Now)</param>
    /// <param name="spec">카메라 번호("0", "1" …) 또는 영상 파일 경로</param>
    /// <param name="outputDir">캡처 이미지를 저장할 폴더(없으면 만든다)</param>
    /// <param name="interval">촬영 간격(최소 50ms)</param>
    public VideoCaptureImageSource(string spec, string outputDir, TimeSpan interval, TimeSpan? readTimeout = null, Func<DateTime>? now = null)
    {
        if (string.IsNullOrWhiteSpace(spec)) throw new ArgumentException("카메라 번호 또는 영상 파일을 지정하세요.", nameof(spec));
        if (interval < TimeSpan.FromMilliseconds(50)) throw new ArgumentOutOfRangeException(nameof(interval), "촬영 간격은 50ms 이상이어야 합니다.");
        _spec = spec.Trim();
        _outputDir = outputDir;
        // "0", "1" 같은 음이 아닌 정수면 카메라 번호로, 그 외(경로 문자열)는 영상 파일로 취급한다.
        _cameraIndex = int.TryParse(_spec, out var idx) && idx >= 0 ? idx : null;
        Interval = interval;
        ReadTimeout = readTimeout ?? TimeSpan.FromSeconds(5);
        _now = now ?? (() => DateTime.Now);
    }

    /// <summary>입력이 카메라 번호이면 true(실시간 촬영 모드), 파일이면 false(파일 끝에서 종료).</summary>
    public bool IsCamera => _cameraIndex is not null;
    /// <summary>이력(회차)에 기록되는 소스 이름(camera:번호 또는 video:경로).</summary>
    public string Name => IsCamera ? $"camera:{_cameraIndex}" : $"video:{_spec}";

    /// <summary>
    /// 소스를 열고 프레임을 PNG로 저장하며 그 경로를 하나씩 내보낸다. 영상 파일은 끝에서 정상 종료, 카메라는 취소될 때까지 계속된다.
    /// </summary>
    public async IAsyncEnumerable<ImageFrame> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (!IsCamera && !File.Exists(_spec)) throw new FileNotFoundException($"영상 파일을 찾을 수 없음: {_spec}", _spec);

        using var cap = IsCamera ? new VideoCapture(_cameraIndex!.Value) : new VideoCapture(_spec);
        if (!cap.IsOpened()) throw new InvalidOperationException(IsCamera
            ? $"카메라 {_cameraIndex}을(를) 열 수 없습니다(연결·다른 프로그램의 사용 여부 확인)."
            : $"영상 파일을 열 수 없습니다(지원하지 않는 형식?): {_spec}");

        Directory.CreateDirectory(_outputDir);
        using var mat = new Mat();   // 프레임 버퍼를 재사용해 매 프레임 메모리를 새로 할당하지 않는다
        int n = 0;                   // 저장한 프레임 순번(파일 이름에 들어간다)

        if (!IsCamera)
        {
            // 프레임 샘플링: 간격(초) x fps = 몇 프레임마다 한 장 저장할지(stride). fps를 알 수 없으면 30으로 가정하고, 최소 1(모든 프레임).
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
            // 카메라 모드: nextShot = 다음 촬영 예정 시각, lastGood = 마지막으로 프레임을 정상 읽은 시각(끊김 감지용)
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
                // 촬영 시각이 되었다: 프레임을 읽는다. 성공하면 다음 촬영 시각을 예약하고 저장해서 내보낸다.
                if (cap.Read(mat) && !mat.Empty())
                {
                    lastGood = _now();
                    nextShot = lastGood + Interval;
                    yield return Save(mat, ++n);
                }
                else
                {
                    // 읽기 실패: 잠깐은 재시도하되, 한도(ReadTimeout)를 넘기면 케이블 분리 등으로 보고 오류를 던져 작업을 Failed로 끝낸다.
                    if (_now() - lastGood > ReadTimeout)
                        throw new IOException($"카메라 {_cameraIndex}에서 {ReadTimeout.TotalSeconds:F0}초 넘게 프레임을 읽지 못했습니다(연결 끊김?).");
                    await Task.Delay(50, ct).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// 프레임을 PNG 파일로 저장하고 그 경로의 ImageFrame을 만든다. 파일 이름에 시각(밀리초)과 순번을 넣어 겹치지 않게 한다.
    /// PNG는 무손실이라 검사 이미지가 압축으로 변형되지 않는다.
    /// </summary>
    private ImageFrame Save(Mat frame, int n)
    {
        var name = $"capture_{_now():yyyyMMdd_HHmmss_fff}_{n:D5}.png";
        var path = Path.Combine(_outputDir, name);
        if (!Cv2.ImEncode(".png", frame, out var buf)) throw new IOException("프레임을 PNG로 인코딩하지 못했습니다.");
        File.WriteAllBytes(path, buf);                                                // 한글 경로에서도 안전하게 바이트로 저장
        return new ImageFrame(path);
    }
}
