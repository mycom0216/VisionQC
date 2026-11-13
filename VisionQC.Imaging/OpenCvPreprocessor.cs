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
    private readonly float[] _mean;
    private readonly float[] _std;

    public OpenCvPreprocessor(float[]? mean = null, float[]? std = null)
    {
        _mean = mean ?? new[] { 0.485f, 0.456f, 0.406f };
        _std = std ?? new[] { 0.229f, 0.224f, 0.225f };
    }

    public PreprocessedImage Process(ImageFrame frame, InspectionSettings settings)
    {
        var bytes = File.ReadAllBytes(frame.Path);
        using var src = Cv2.ImDecode(bytes, ImreadModes.Color);
        if (src.Empty())
            throw new InvalidDataException($"이미지를 읽을 수 없음: {Path.GetFileName(frame.Path)}");

        int size = settings.InputSize;
        using var resized = new Mat();
        Cv2.Resize(src, resized, new Size(size, size), 0, 0, InterpolationFlags.Area);

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
