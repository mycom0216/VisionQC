using VisionQC.Core;

namespace VisionQC.Tests;

public class JudgeTests
{
    private static readonly ThresholdJudge Judge = new();
    private static InspectionSettings S(float t = 0.5f) => new() { Threshold = t };

    [Theory]
    [InlineData(0.49f, Verdict.OK)]
    [InlineData(0.50f, Verdict.NG)]   // 경계값은 NG
    [InlineData(0.99f, Verdict.NG)]
    [InlineData(0.0f, Verdict.OK)]
    public void Score_boundary(float score, Verdict expected)
        => Assert.Equal(expected, Judge.Decide(InferenceOutput.FromScore(score), S()).Verdict);

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Invalid_score_is_error_not_ok(float score)
        => Assert.Equal(Verdict.ERROR, Judge.Decide(InferenceOutput.FromScore(score), S()).Verdict);

    [Fact]
    public void Detections_below_threshold_are_ok_and_filtered()
    {
        var o = new InferenceOutput(0, new[] { new Detection("scratch", 0.3f, 1, 1, 2, 2) });
        var j = Judge.Decide(o, S(0.5f));
        Assert.Equal(Verdict.OK, j.Verdict);
        Assert.Empty(j.Defects);
    }

    [Fact]
    public void Detection_at_or_above_threshold_is_ng_with_only_those_defects()
    {
        var o = new InferenceOutput(0, new[]
        {
            new Detection("scratch", 0.3f, 0, 0, 1, 1),
            new Detection("short", 0.8f, 5, 5, 3, 3),
        });
        var j = Judge.Decide(o, S(0.5f));
        Assert.Equal(Verdict.NG, j.Verdict);
        Assert.Single(j.Defects);
        Assert.Equal("short", j.Defects[0].Type);
        Assert.Equal(0.8f, j.Score);
    }

    [Fact]
    public void Rejudge_with_different_threshold_needs_no_model()
    {
        var o = InferenceOutput.FromScore(0.6f);
        Assert.Equal(Verdict.NG, Judge.Decide(o, S(0.5f)).Verdict);
        Assert.Equal(Verdict.OK, Judge.Decide(o, S(0.7f)).Verdict);
    }
}
