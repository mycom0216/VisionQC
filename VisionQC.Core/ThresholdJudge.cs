// ============================================================================
// ThresholdJudge.cs - 임계값(threshold) 기반 최종 판정
//
// 파이프라인에서의 위치: ONNX 추론이 낸 "불량 점수"를 사람이 정한 기준선(임계값)과 비교해
// OK / NG / ERROR 로 바꾸는 마지막 계산 단계다. 모델은 확률만 주고, 어디서 자르느냐는 설정(임계값)으로 분리해
// 모델을 다시 학습하지 않고도 현장에서 민감도를 조정할 수 있게 했다.
// 임계값을 낮추면 불량을 더 잘 잡지만 양품을 NG로 오검출하는 일이 늘고, 높이면 그 반대다.
// ============================================================================
namespace VisionQC.Core;

/// <summary>
/// 임계값 판정. 검출 결과가 있으면 "신뢰도 ≥ 임계값인 박스가 하나라도 있으면 NG",
/// 없으면 점수(불량 확률) ≥ 임계값이면 NG. 점수가 NaN/무한대면 판정 불능(ERROR).
/// 경계값(점수 == 임계값)은 NG — 미검출이 오검출보다 비용이 크다는 분석서 §5 기준.
/// </summary>
public sealed class ThresholdJudge : IJudge
{
    // 판정 순서: (1) 점수가 NaN/무한대면 비교가 무의미하므로 ERROR  (2) 검출 박스가 있으면 박스 기준  (3) 없으면 분류 점수 기준.
    /// <summary>모델 출력 1건과 설정(임계값)으로 판정을 만든다. 입력이 비정상이면 예외 대신 ERROR 판정을 돌려준다.</summary>
    public Judgement Decide(InferenceOutput output, InspectionSettings settings)
    {
        // 모델이 깨진 값을 내면 "OK"로 오인되지 않도록 반드시 ERROR 로 돌린다(NaN 비교는 항상 false 라 OK 가 되어 버림).
        if (!float.IsFinite(output.Score))
            return new Judgement(Verdict.ERROR, output.Score, Array.Empty<Detection>(), "점수가 유효하지 않음");

        if (output.Detections.Count > 0)
        {
            // 검출 모델 경로: 임계값 이상 신뢰도(Confidence)의 박스만 불량으로 인정한다.
            // 요약 점수는 가장 높은 신뢰도(임계값 미만이어도) - 화면에서 "얼마나 아슬아슬했는지" 볼 수 있게 한다.
            var defects = output.Detections.Where(d => d.Confidence >= settings.Threshold).ToList();
            var score = output.Detections.Max(d => d.Confidence);
            return new Judgement(defects.Count > 0 ? Verdict.NG : Verdict.OK, score, defects);
        }

        // 분류 모델 경로: 경계값(==)은 NG. 불량을 놓치는 비용이 양품을 의심하는 비용보다 크다는 기준 때문이다.
        return new Judgement(output.Score >= settings.Threshold ? Verdict.NG : Verdict.OK,
            output.Score, Array.Empty<Detection>());
    }
}
