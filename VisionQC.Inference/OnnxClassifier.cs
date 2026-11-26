// ============================================================================
// OnnxClassifier.cs - ONNX Runtime 으로 분류 모델을 실행하는 추론 엔진(IInferenceEngine 구현)
//
// 파이프라인에서의 위치: 이미지 공급원 -> 전처리 -> [여기: ONNX 추론] -> 임계값 판정 -> 저장/UI
// ONNX = 다양한 학습 프레임워크(PyTorch 등)의 모델을 공통 형식으로 저장한 파일. ONNX Runtime 이 이를 C#에서 실행한다.
// 이 클래스의 책임은 "전처리된 float 배열 -> 불량 확률(0~1)"까지다. 양품/불량 판정(임계값 비교)은 ThresholdJudge 가 따로 한다.
// 그래서 모델을 바꾸지 않고도 임계값만 조정할 수 있다.
// ============================================================================
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
    // ONNX Runtime 세션(모델을 메모리에 올린 객체). 로딩이 비싸므로 한 번 만들어 모든 워커가 공유한다.
    private readonly InferenceSession _session;
    // 모델 입력 텐서의 이름. 모델마다 다르므로 로딩 시 메타데이터에서 읽는다.
    private readonly string _inputName;

    /// <summary>모델 식별 정보(파일명 + 해시 앞 12자리 버전 + 종류 "classification").</summary>
    public ModelInfo Model { get; }

    /// <summary>모델 파일을 읽어 세션을 만든다. 파일이 없으면 FileNotFoundException. 잘못된 ONNX 파일이면 ONNX Runtime 이 예외를 던진다.</summary>
    public OnnxClassifier(string modelPath)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"모델 파일이 없음: {modelPath}", modelPath);

        _session = new InferenceSession(modelPath);
        // 입력이 하나뿐인 분류 모델을 가정하고 첫 입력의 이름을 쓴다.
        _inputName = _session.InputMetadata.Keys.First();
        Model = new ModelInfo(Path.GetFileName(modelPath), Sha256Prefix(modelPath), "classification");
    }

    /// <summary>
    /// 전처리된 이미지 1장을 추론해 불량 확률을 돌려준다(CAM 출력이 있으면 히트맵도 함께).
    /// 입력 텐서 모양 [1,3,H,W] = 배치 1장, 채널 3(RGB), 높이, 너비.
    /// </summary>
    public InferenceOutput Run(PreprocessedImage image)
    {
        // 전처리 결과 배열을 복사 없이 텐서로 감싼다(Data 는 이미 CHW 순서).
        var tensor = new DenseTensor<float>(image.Data, new[] { 1, 3, image.Height, image.Width });
        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) });
        // 출력은 IDisposable 이므로 using 으로 해제한다. 첫 출력(logits)을 평평한 배열로 읽는다.
        // logits = 확률로 바꾸기 전의 원시 점수(소프트맥스/시그모이드를 거치기 전).
        var values = results.First().AsEnumerable<float>().ToArray();       // 첫 출력 = logits

        // 선택 출력 "cam"(training/add_cam_output.py가 덧붙임)이 있으면 불량 위치 히트맵을 함께 돌려준다. 없으면 null.
        Heatmap? heatmap = null;
        var cam = results.FirstOrDefault(r => r.Name == "cam");
        if (cam is not null)
        {
            var t = cam.AsTensor<float>();
            // 히트맵은 [1,1,H,W] 모양일 때만 인정한다(배치 1, 채널 1). 다른 모양이면 무시해 잘못된 그림이 나오는 것을 막는다.
            // Dimensions[3] = 가로(열) 수, Dimensions[2] = 세로(행) 수 - Heatmap(Width, Height, ...) 순서에 맞춘 것이다.
            if (t.Dimensions.Length == 4 && t.Dimensions[0] == 1 && t.Dimensions[1] == 1)
                heatmap = new Heatmap(t.Dimensions[3], t.Dimensions[2], t.ToArray());
        }
        return InferenceOutput.FromScore(ToDefectProbability(values), heatmap);
    }

    /// <summary>
    /// 모델 출력(logits)을 "불량일 확률"로 바꾼다.
    ///  - 비어 있으면 NaN(판정기가 ERROR 로 처리)
    ///  - 값 1개: 시그모이드 1/(1+e^-x) 로 0~1 확률화
    ///  - 값 2개 이상: 소프트맥스 후 인덱스 1(불량 클래스)의 확률
    /// internal 인 이유: 단위 테스트에서 직접 검증하기 위해서다.
    /// </summary>
    internal static float ToDefectProbability(float[] values)
    {
        if (values.Length == 0) return float.NaN;
        if (values.Length == 1) return 1f / (1f + MathF.Exp(-values[0]));

        // 소프트맥스: exp(v_i) / sum(exp(v_j)). 최댓값을 먼저 빼 주면 결과는 같고 exp 가 매우 커져 무한대가 되는 오버플로를 피한다.
        float max = values.Max();                               // 오버플로 방지
        float sum = values.Sum(v => MathF.Exp(v - max));
        return MathF.Exp(values[1] - max) / sum;
    }

    // 모델 파일 전체의 SHA-256 해시 앞 12자리(소문자 16진수). 파일 내용이 한 바이트라도 다르면 버전이 달라져
    // 어떤 모델 파일로 판정했는지 이력에서 추적할 수 있다(이름이 같아도 구분됨).
    private static string Sha256Prefix(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs))[..12].ToLowerInvariant();
    }

    /// <summary>네이티브 메모리를 쓰는 ONNX 세션을 해제한다.</summary>
    public void Dispose() => _session.Dispose();
}
