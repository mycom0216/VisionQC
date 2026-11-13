namespace VisionQC.Core;

/// <summary>판정. ERROR는 "판정 불능"이며 양품으로 취급하지 않는다.</summary>
public enum Verdict { OK, NG, ERROR }

public enum JobStatus { Running, Completed, Cancelled, Failed }

public sealed record ImageFrame(string Path);

/// <summary>전처리된 이미지. Data는 RGB, CHW 순서, 정규화된 float.</summary>
public sealed record PreprocessedImage(string Path, float[] Data, int Width, int Height, int OriginalWidth, int OriginalHeight);

/// <summary>검출된 불량 1건. 좌표는 모델 입력 크기 기준.</summary>
public sealed record Detection(string Type, float Confidence, int X, int Y, int W, int H);

/// <summary>모델 출력. 분류 모델은 Score(불량 확률)만, 검출 모델은 Detections를 채운다.</summary>
public sealed record InferenceOutput(float Score, IReadOnlyList<Detection> Detections, Heatmap? Heatmap = null)
{
    public static InferenceOutput FromScore(float score, Heatmap? heatmap = null) => new(score, Array.Empty<Detection>(), heatmap);
}

public sealed record Judgement(Verdict Verdict, float Score, IReadOnlyList<Detection> Defects, string? Reason = null);

public sealed record ModelInfo(string Name, string Version, string TaskType);

public sealed record InspectionSettings
{
    public float Threshold { get; init; } = 0.5f;
    public int InputSize { get; init; } = 224;
    /// <summary>읽기 큐 용량. 가득 차면 읽는 쪽이 기다린다(메모리 폭주 방지).</summary>
    public int QueueCapacity { get; init; } = 8;
    public int Workers { get; init; } = 1;
    /// <summary>관심 영역(비율). null이면 이미지 전체. 학습 때와 같은 영역이어야 한다.</summary>
    public Roi? Roi { get; init; }
}

public sealed record InspectionResult
{
    public long Id { get; init; }
    public long JobId { get; init; }
    public required string ImagePath { get; init; }
    public required Verdict Verdict { get; init; }
    public float Score { get; init; }
    public int ElapsedMs { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTime InspectedAtUtc { get; init; } = DateTime.UtcNow;
    public IReadOnlyList<Detection> Defects { get; init; } = Array.Empty<Detection>();
    /// <summary>불량 위치 히트맵(CAM 출력이 있는 모델일 때). 이력에는 NG 결과만 저장한다.</summary>
    public Heatmap? Heatmap { get; init; }
}

public sealed record HistoryFilter(DateTime? FromUtc = null, DateTime? ToUtc = null, Verdict? Verdict = null, int Limit = 500);

public sealed record RunSummary(long JobId, JobStatus Status, int Total, int Ok, int Ng, int Error, int SaveFailures, string? FatalError);
