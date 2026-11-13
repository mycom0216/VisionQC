namespace VisionQC.Core;

/// <summary>
/// 임계값 판정. 검출 결과가 있으면 "신뢰도 ≥ 임계값인 박스가 하나라도 있으면 NG",
/// 없으면 점수(불량 확률) ≥ 임계값이면 NG. 점수가 NaN/무한대면 판정 불능(ERROR).
/// 경계값(점수 == 임계값)은 NG — 미검출이 오검출보다 비용이 크다는 분석서 §5 기준.
/// </summary>
public sealed class ThresholdJudge : IJudge
{
    public Judgement Decide(InferenceOutput output, InspectionSettings settings)
    {
        if (!float.IsFinite(output.Score))
            return new Judgement(Verdict.ERROR, output.Score, Array.Empty<Detection>(), "점수가 유효하지 않음");

        if (output.Detections.Count > 0)
        {
            var defects = output.Detections.Where(d => d.Confidence >= settings.Threshold).ToList();
            var score = output.Detections.Max(d => d.Confidence);
            return new Judgement(defects.Count > 0 ? Verdict.NG : Verdict.OK, score, defects);
        }

        return new Judgement(output.Score >= settings.Threshold ? Verdict.NG : Verdict.OK,
            output.Score, Array.Empty<Detection>());
    }
}
