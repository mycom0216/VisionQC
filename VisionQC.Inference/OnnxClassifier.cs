using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using VisionQC.Core;

namespace VisionQC.Inference;

/// <summary>
/// ONNX 분류 모델. 입력 [1,3,H,W] float. 출력은 첫 번째 출력 텐서의 값으로 해석한다:
///  - 값 2개 이상: logits로 보고 softmax 후 인덱스 1을 "불량 확률"로 사용(인덱스 0=양품, 1=불량 가정)
///  - 값 1개: logit으로 보고 sigmoid
/// 학습·내보내기 때 이 규약(클래스 순서, logits 출력)을 지켜야 한다.
/// 모델 버전은 파일 SHA-256 앞 12자리로 자동 부여해 판정 이력에 남긴다(추적성).
/// InferenceSession은 스레드 안전하므로 1개를 공유한다.
/// </summary>
public sealed class OnnxClassifier : IInferenceEngine, IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;

    public ModelInfo Model { get; }

    public OnnxClassifier(string modelPath)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"모델 파일이 없음: {modelPath}", modelPath);

        _session = new InferenceSession(modelPath);
        _inputName = _session.InputMetadata.Keys.First();
        Model = new ModelInfo(Path.GetFileName(modelPath), Sha256Prefix(modelPath), "classification");
    }

    public InferenceOutput Run(PreprocessedImage image)
    {
        var tensor = new DenseTensor<float>(image.Data, new[] { 1, 3, image.Height, image.Width });
        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) });
        var values = results.First().AsEnumerable<float>().ToArray();
        return InferenceOutput.FromScore(ToDefectProbability(values));
    }

    internal static float ToDefectProbability(float[] values)
    {
        if (values.Length == 0) return float.NaN;
        if (values.Length == 1) return 1f / (1f + MathF.Exp(-values[0]));

        float max = values.Max();                               // 오버플로 방지
        float sum = values.Sum(v => MathF.Exp(v - max));
        return MathF.Exp(values[1] - max) / sum;
    }

    private static string Sha256Prefix(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs))[..12].ToLowerInvariant();
    }

    public void Dispose() => _session.Dispose();
}
