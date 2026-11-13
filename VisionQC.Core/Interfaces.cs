namespace VisionQC.Core;

/// <summary>이미지 공급원(폴더, 카메라 등). 교체해도 파이프라인은 그대로.</summary>
public interface IImageSource
{
    string Name { get; }
    IAsyncEnumerable<ImageFrame> ReadAsync(CancellationToken ct);
}

public interface IPreprocessor
{
    /// <summary>읽을 수 없는 이미지면 예외를 던진다(파이프라인이 ERROR로 기록).</summary>
    PreprocessedImage Process(ImageFrame frame, InspectionSettings settings);
}

public interface IInferenceEngine
{
    ModelInfo Model { get; }
    InferenceOutput Run(PreprocessedImage image);
}

public interface IJudge
{
    Judgement Decide(InferenceOutput output, InspectionSettings settings);
}

public interface IResultRepository
{
    Task<long> BeginJobAsync(ModelInfo model, string sourceName, float threshold, CancellationToken ct = default);
    Task EndJobAsync(long jobId, JobStatus status, CancellationToken ct = default);
    Task SaveAsync(InspectionResult result, CancellationToken ct = default);
    Task<IReadOnlyList<InspectionResult>> QueryAsync(HistoryFilter filter, CancellationToken ct = default);
}
