// ============================================================================
// OpenCvPreprocessor.cs
// 역할: 이미지 파일 1장을 ONNX 분류 모델이 요구하는 입력 텐서(float 배열)로 바꾸는 전처리기.
// 파이프라인에서의 위치:
//   이미지 소스 -> [전처리: 여기] -> 추론(ONNX) -> 판정 -> 이력 저장소 / UI
// 왜 중요한가: 모델은 "학습할 때와 똑같은 방식"으로 만든 입력만 올바르게 분류한다. 크기·색 순서·정규화가 하나라도 다르면
//   조용히 엉뚱한 점수가 나온다. 그래서 이 파일의 순서(리사이즈, BGR→RGB, /255, 평균/표준편차 정규화, CHW 배치)를 학습 스크립트와 맞춰야 한다.
// ============================================================================
using OpenCvSharp;
using VisionQC.Core;

namespace VisionQC.Imaging;

/// <summary>
/// 이미지 로드 → 정사각 리사이즈 → BGR→RGB → /255 → (x-mean)/std → CHW float.
/// 기본 mean/std는 ImageNet 값. 학습 때와 다르면 모델이 틀린 입력을 받으므로 학습 스크립트와 맞춰야 한다.
/// 파일은 ImDecode로 읽어 한글 경로에서도 동작한다.
/// </summary>
public sealed class OpenCvPreprocessor : IPreprocessor
{
    // 채널별(R,G,B 순) 정규화 평균과 표준편차. 학습 때 쓴 값과 같아야 한다.
    private readonly float[] _mean;
    private readonly float[] _std;

    /// <param name="mean">RGB 채널 평균(생략 시 ImageNet 값)</param>
    /// <param name="std">RGB 채널 표준편차(생략 시 ImageNet 값)</param>
    public OpenCvPreprocessor(float[]? mean = null, float[]? std = null)
    {
        _mean = mean ?? new[] { 0.485f, 0.456f, 0.406f };
        _std = std ?? new[] { 0.229f, 0.224f, 0.225f };
    }

    /// <summary>
    /// 이미지 파일을 읽어 모델 입력으로 변환한다.
    /// 처리 순서: ① 파일 바이트 읽기·디코드 ② (설정돼 있으면) ROI 영역만 잘라내기 ③ 정사각 리사이즈 ④ 픽셀을 R,G,B 평면 순서(CHW)로 정규화.
    /// ROI(관심 영역, Region Of Interest) = 이미지 전체가 아니라 검사해야 할 부분만 지정한 사각형. 배경 등 불필요한 부분이 판정에 영향을 주지 않게 한다.
    /// 읽을 수 없는 파일은 예외를 던지며, 상위 파이프라인이 이를 ERROR 판정으로 기록한다.
    /// </summary>
    /// <param name="frame">검사할 이미지(파일 경로)</param>
    /// <param name="settings">입력 크기(InputSize)·ROI 등 검사 설정</param>
    /// <returns>정규화된 CHW float 데이터와 입력 크기, 원본 이미지 크기(불량 박스를 원본 좌표로 되돌릴 때 필요)</returns>
    public PreprocessedImage Process(ImageFrame frame, InspectionSettings settings)
    {
        // Cv2.ImRead는 한글 등 비ASCII 경로에서 실패할 수 있어, 파일을 직접 바이트로 읽은 뒤 메모리에서 디코드한다.
        var bytes = File.ReadAllBytes(frame.Path);
        using var src = Cv2.ImDecode(bytes, ImreadModes.Color);   // Color: 흑백 이미지도 3채널(BGR)로 통일
        if (src.Empty())
            throw new InvalidDataException($"이미지를 읽을 수 없음: {Path.GetFileName(frame.Path)}");

        // ROI가 있으면 그 영역만 잘라서 리사이즈한다(비율 → 픽셀, 항상 이미지 안쪽·최소 1px)
        Mat? crop = null;
        Mat source = src;
        if (settings.Roi is { } roi)
        {
            if (roi.Validate() is { } problem) throw new InvalidOperationException($"ROI 설정 오류: {problem}");
            var (rx, ry, rw, rh) = roi.ToPixels(src.Width, src.Height);
            crop = new Mat(src, new Rect(rx, ry, rw, rh));   // 복사 없이 src의 일부를 가리키는 뷰(src가 살아 있는 동안만 유효)
            source = crop;
        }

        int size = settings.InputSize;   // 모델이 요구하는 한 변 길이(정사각형)
        using var resized = new Mat();
        // Area 보간: 축소할 때 주변 픽셀을 평균 내어 얇은 결함 신호가 사라지거나 계단처럼 깨지는 것을 줄인다.
        try { Cv2.Resize(source, resized, new Size(size, size), 0, 0, InterpolationFlags.Area); }
        finally { crop?.Dispose(); }

        // 결과 레이아웃은 CHW(채널 3개 평면이 차례로 이어짐: [R평면][G평면][B평면]). 한 평면은 size*size개 값이다.
        resized.GetArray(out Vec3b[] pixels);
        int plane = size * size;
        var data = new float[3 * plane];
        for (int i = 0; i < plane; i++)
        {
            var p = pixels[i];                       // BGR
            data[i] = (p.Item2 / 255f - _mean[0]) / _std[0];              // R
            data[plane + i] = (p.Item1 / 255f - _mean[1]) / _std[1];      // G
            data[2 * plane + i] = (p.Item0 / 255f - _mean[2]) / _std[2];  // B
        }
        return new PreprocessedImage(frame.Path, data, size, size, src.Width, src.Height);
    }
}
