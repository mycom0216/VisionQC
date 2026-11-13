using VisionQC.Core;
using VisionQC.Imaging;
using VisionQC.Inference;

namespace VisionQC.Tests;

/// <summary>
/// 파이썬(training/)이 내보낸 ONNX를 C# 전처리·추론으로 돌렸을 때 파이썬과 같은 점수가 나오는지 확인한다.
/// 모델 파일이 필요하므로 환경변수가 없으면 아무것도 하지 않고 통과한다(CI에서 모델 없이 실행 가능).
///   VISIONQC_PARITY_MODEL    = out/model.onnx
///   VISIONQC_PARITY_CASES    = "이미지경로|파이썬점수;이미지경로|파이썬점수"  (training/parity_cases.py 출력)
/// </summary>
public class OnnxParityTests
{
    [Fact]
    public void Csharp_scores_match_python_scores()
    {
        var model = Environment.GetEnvironmentVariable("VISIONQC_PARITY_MODEL");
        var cases = Environment.GetEnvironmentVariable("VISIONQC_PARITY_CASES");
        if (string.IsNullOrEmpty(model) || string.IsNullOrEmpty(cases)) return;

        var settings = new InspectionSettings { InputSize = 224 };
        var pre = new OpenCvPreprocessor();
        using var engine = new OnnxClassifier(model);

        foreach (var c in cases.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = c.Split('|');
            var expected = float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            var actual = engine.Run(pre.Process(new ImageFrame(parts[0]), settings)).Score;
            // 리사이즈 구현(OpenCV 동일 계열)·float 연산 차이만 허용
            Assert.True(Math.Abs(expected - actual) < 1e-4, $"{parts[0]}: python {expected} vs C# {actual}");
        }
    }
}
