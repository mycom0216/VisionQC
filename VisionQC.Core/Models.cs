// ============================================================================
// Models.cs - 검사 파이프라인 전체가 주고받는 데이터 모델(값 객체) 모음
//
// 파이프라인에서의 위치:
//   이미지 공급원(ImageFrame) -> 전처리(PreprocessedImage) -> ONNX 추론(InferenceOutput)
//   -> 임계값 판정(Judgement) -> 결과 저장소/화면(InspectionResult)
// 모든 타입은 불변(immutable) record 이다. 단계 사이에서 값이 몰래 바뀌지 않게 하고,
// 여러 워커 스레드가 동시에 처리해도 안전하게 하기 위한 설계다.
// ============================================================================
namespace VisionQC.Core;

/// <summary>판정. ERROR는 "판정 불능"이며 양품으로 취급하지 않는다.</summary>
// 판정 용어: OK = 양품, NG(No Good) = 불량, ERROR = 이미지를 읽지 못했거나 점수가 NaN 등이라 판단 자체를 못 한 상태.
// ERROR를 OK로 두면 불량이 조용히 통과할 수 있으므로 별도 값으로 분리했다(통계의 양품률 분모에서도 제외).
public enum Verdict { OK, NG, ERROR }

/// <summary>검사 작업(Job, 폴더 1회 실행 단위)의 진행 상태. 실행 중 / 정상 완료 / 사용자 취소 / 치명적 오류로 실패.</summary>
public enum JobStatus { Running, Completed, Cancelled, Failed }

/// <summary>이미지 공급원이 파이프라인에 넘기는 "아직 읽지 않은" 이미지 1장. 파일 경로만 들고 있고 디코딩은 전처리 단계가 한다.</summary>
public sealed record ImageFrame(string Path);

/// <summary>전처리된 이미지. Data는 RGB, CHW 순서, 정규화된 float.</summary>
// Data 배열 크기 = 3 * Height * Width. CHW 는 "채널(R,G,B) -> 세로 -> 가로" 순서로 나열한 ONNX 표준 입력 배치다.
// OriginalWidth/Height 는 ROI 자르기·리사이즈 전의 원본 크기로, 검출 좌표를 원본으로 환산할 때 필요하다.
public sealed record PreprocessedImage(string Path, float[] Data, int Width, int Height, int OriginalWidth, int OriginalHeight);

/// <summary>검출된 불량 1건. 좌표는 모델 입력 크기 기준.</summary>
public sealed record Detection(string Type, float Confidence, int X, int Y, int W, int H);

/// <summary>모델 출력. 분류 모델은 Score(불량 확률)만, 검출 모델은 Detections를 채운다.</summary>
// Score = 불량 점수(불량 확률, 0~1). Detections = 박스 검출 모델일 때만 채워진다.
// Heatmap = CAM(Class Activation Map, 모델이 어느 위치를 보고 불량이라 했는지 나타내는 격자) - 지원 모델에서만 존재.
public sealed record InferenceOutput(float Score, IReadOnlyList<Detection> Detections, Heatmap? Heatmap = null)
{
    // 분류 모델처럼 점수 하나만 나오는 경우를 위한 편의 생성 메서드(검출 목록은 빈 배열).
    public static InferenceOutput FromScore(float score, Heatmap? heatmap = null) => new(score, Array.Empty<Detection>(), heatmap);
}

/// <summary>
/// 판정기(IJudge)의 결과. Verdict = 최종 판정, Score = 판정에 쓴 불량 점수,
/// Defects = 임계값을 넘은 검출 목록(분류 모델이면 빈 목록), Reason = ERROR일 때의 사유.
/// </summary>
public sealed record Judgement(Verdict Verdict, float Score, IReadOnlyList<Detection> Defects, string? Reason = null);

/// <summary>
/// 사용한 모델의 식별 정보. 판정 이력에 같이 남겨 "어떤 모델로 판정했는가"를 추적한다(추적성).
/// Name = 파일명, Version = 파일 해시 앞부분, TaskType = "classification" 등 모델 종류.
/// </summary>
public sealed record ModelInfo(string Name, string Version, string TaskType);

/// <summary>
/// 파이프라인 1회 실행에 필요한 설정값 묶음(앱 설정 AppSettings에서 변환되어 전달된다).
/// 임계값(Threshold)은 "불량 점수가 이 값 이상이면 NG"로 보는 기준선이다.
/// </summary>
public sealed record InspectionSettings
{
    /// <summary>불량 판정 임계값(0~1). 점수가 이 값 이상이면 NG. 기본 0.5.</summary>
    public float Threshold { get; init; } = 0.5f;
    /// <summary>모델 입력 한 변의 픽셀 수(정사각형으로 리사이즈). 모델 학습 때의 입력 크기와 같아야 한다.</summary>
    public int InputSize { get; init; } = 224;
    /// <summary>읽기 큐 용량. 가득 차면 읽는 쪽이 기다린다(메모리 폭주 방지).</summary>
    public int QueueCapacity { get; init; } = 8;
    /// <summary>동시에 추론하는 워커(소비자) 수. 기본 1이면 순차 처리, 늘리면 처리량이 늘지만 CPU/메모리를 더 쓴다.</summary>
    public int Workers { get; init; } = 1;
    /// <summary>관심 영역(비율). null이면 이미지 전체. 학습 때와 같은 영역이어야 한다.</summary>
    public Roi? Roi { get; init; }
}

/// <summary>
/// 이미지 1장의 검사 결과. 저장소에 저장되고 이력·통계 화면에서 조회된다.
/// 원 판정(Verdict)은 불변이며, 사람이 고친 판정은 별도 Review 로 쌓는다(Review.cs 참고).
/// </summary>
public sealed record InspectionResult
{
    /// <summary>저장소가 부여하는 결과 ID(저장 전에는 0).</summary>
    public long Id { get; init; }
    /// <summary>이 결과가 속한 검사 작업(Job) ID.</summary>
    public long JobId { get; init; }
    public required string ImagePath { get; init; }
    public required Verdict Verdict { get; init; }
    /// <summary>불량 점수(0~1, 클수록 불량 가능성이 높음). ERROR이면 의미 없는 값.</summary>
    public float Score { get; init; }
    /// <summary>전처리+추론+판정에 걸린 시간(밀리초). 통계의 처리시간 분포에 쓰인다.</summary>
    public int ElapsedMs { get; init; }
    /// <summary>ERROR 판정일 때의 사유(읽기 실패 등). 정상 판정이면 null.</summary>
    public string? ErrorMessage { get; init; }
    /// <summary>검사 시각. 저장은 항상 UTC로 하고 화면에서 로컬 시간대로 바꿔 보여 준다.</summary>
    public DateTime InspectedAtUtc { get; init; } = DateTime.UtcNow;
    /// <summary>임계값을 넘은 불량 검출 목록(검출 모델용). 분류 모델은 비어 있다.</summary>
    public IReadOnlyList<Detection> Defects { get; init; } = Array.Empty<Detection>();
    /// <summary>불량 위치 히트맵(CAM 출력이 있는 모델일 때). 이력에는 NG 결과만 저장한다.</summary>
    public Heatmap? Heatmap { get; init; }
}

/// <summary>이력 조회 조건. 기간(UTC)·판정 종류로 거르고 Limit(기본 500)건까지만 가져와 화면이 느려지는 것을 막는다.</summary>
public sealed record HistoryFilter(DateTime? FromUtc = null, DateTime? ToUtc = null, Verdict? Verdict = null, int Limit = 500);

/// <summary>
/// 검사 1회 실행이 끝난 뒤의 요약. Total = Ok + Ng + Error.
/// SaveFailures = 결과 저장에 실패한 건수(검사는 계속하되 숫자로 알린다), FatalError = 이미지 공급원이 치명적으로 실패했을 때의 메시지.
/// </summary>
public sealed record RunSummary(long JobId, JobStatus Status, int Total, int Ok, int Ng, int Error, int SaveFailures, string? FatalError);
