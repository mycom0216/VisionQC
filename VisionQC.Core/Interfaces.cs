// ============================================================================
// Interfaces.cs - 파이프라인 각 단계의 계약(인터페이스) 정의
//
// 이미지 공급원(IImageSource) -> 전처리(IPreprocessor) -> 추론(IInferenceEngine)
//   -> 판정(IJudge) -> 저장(IResultRepository)
// 단계마다 인터페이스로 분리한 이유: 폴더 대신 카메라를, CPU 대신 다른 엔진을, SQLite 대신 다른 DB를
// 파이프라인(InspectionPipeline) 코드를 고치지 않고 교체할 수 있고, 테스트에서는 가짜(Fake) 구현을 끼울 수 있다.
// ============================================================================
namespace VisionQC.Core;

/// <summary>이미지 공급원(폴더, 카메라 등). 교체해도 파이프라인은 그대로.</summary>
public interface IImageSource
{
    string Name { get; }
    IAsyncEnumerable<ImageFrame> ReadAsync(CancellationToken ct);
}

/// <summary>전처리 단계. 이미지 파일을 읽어 ROI 자르기·리사이즈·정규화를 거쳐 모델 입력 형태로 바꾼다.</summary>
public interface IPreprocessor
{
    /// <summary>읽을 수 없는 이미지면 예외를 던진다(파이프라인이 ERROR로 기록).</summary>
    // settings 에는 입력 크기(InputSize)와 ROI가 들어 있어 학습 때와 같은 방식으로 전처리하게 한다.
    PreprocessedImage Process(ImageFrame frame, InspectionSettings settings);
}

/// <summary>추론 엔진(예: ONNX 모델). 전처리된 이미지를 받아 불량 점수(와 선택적으로 히트맵)를 돌려준다.</summary>
public interface IInferenceEngine
{
    /// <summary>이 엔진이 쓰는 모델의 식별 정보(이력에 기록되어 추적성을 보장한다).</summary>
    ModelInfo Model { get; }
    /// <summary>이미지 1장을 추론한다. 여러 워커가 동시에 호출할 수 있으므로 구현은 스레드 안전해야 한다.</summary>
    InferenceOutput Run(PreprocessedImage image);
}

/// <summary>판정 단계. 모델 출력과 임계값 설정을 받아 OK/NG/ERROR 를 결정한다(구현: ThresholdJudge).</summary>
public interface IJudge
{
    Judgement Decide(InferenceOutput output, InspectionSettings settings);
}

/// <summary>
/// 검사 결과 저장소(구현은 VisionQC.Data 의 SQLite). 검사 작업(Job) 시작/종료 기록, 결과 저장·조회,
/// 통계용 조회, 사람이 고친 판정(Review) 기록을 담당한다.
/// </summary>
public interface IResultRepository
{
    /// <summary>검사 작업 1건을 시작으로 기록하고 Job ID를 돌려준다. 사용한 모델·소스 이름·임계값을 같이 남겨 나중에 재현할 수 있게 한다.</summary>
    Task<long> BeginJobAsync(ModelInfo model, string sourceName, float threshold, CancellationToken ct = default);
    /// <summary>검사 작업의 종료 상태(완료/취소/실패)를 기록한다.</summary>
    Task EndJobAsync(long jobId, JobStatus status, CancellationToken ct = default);
    /// <summary>결과 1건을 저장한다.</summary>
    Task SaveAsync(InspectionResult result, CancellationToken ct = default);
    /// <summary>필터에 맞는 이력(원 판정)을 조회한다.</summary>
    Task<IReadOnlyList<InspectionResult>> QueryAsync(HistoryFilter filter, CancellationToken ct = default);
    /// <summary>통계용 원본 행과 불량 유형별 건수(집계는 StatsCalculator가 한다).</summary>
    Task<StatsData> QueryStatsAsync(StatsFilter filter, CancellationToken ct = default);
    /// <summary>정정 1건 추가. 존재하지 않는 결과 ID면 예외. 원 판정은 바꾸지 않는다.</summary>
    Task AddReviewAsync(Review review, CancellationToken ct = default);
    /// <summary>이력 조회 + 결과마다 가장 나중 정정과 정정 횟수.</summary>
    Task<IReadOnlyList<ReviewedResult>> QueryReviewedAsync(HistoryFilter filter, CancellationToken ct = default);
    /// <summary>한 결과의 정정 이력(오래된 순).</summary>
    Task<IReadOnlyList<Review>> GetReviewsAsync(long resultId, CancellationToken ct = default);
}
