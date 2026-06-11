namespace Mneme.Contracts.Tests;

public sealed class EnumTests
{
    [Theory]
    [InlineData(EpistemicCategory.Evidence, 0)]
    [InlineData(EpistemicCategory.Fact, 1)]
    [InlineData(EpistemicCategory.Decision, 2)]
    [InlineData(EpistemicCategory.Hypothesis, 3)]
    [InlineData(EpistemicCategory.Goal, 4)]
    [InlineData(EpistemicCategory.Action, 5)]
    [InlineData(EpistemicCategory.Outcome, 6)]
    public void EpistemicCategory_HasStableNumericValues(EpistemicCategory category, int expected)
    {
        // Stability of the underlying numeric values matters for SQL storage.
        Assert.Equal(expected, (int)category);
    }

    [Fact]
    public void EpistemicCategory_HasExactlySevenValues()
    {
        // Lock the count so adding a new category requires updating tests
        // and the plan together.
        Assert.Equal(7, Enum.GetValues<EpistemicCategory>().Length);
    }

    [Theory]
    [InlineData(EventChannel.Epistemic, 0)]
    [InlineData(EventChannel.Technical, 1)]
    public void EventChannel_HasStableNumericValues(EventChannel channel, int expected)
    {
        Assert.Equal(expected, (int)channel);
    }

    [Fact]
    public void Classification_DefaultIsPublic()
    {
        // The default(Classification) struct value should be Public so
        // explicitly-classified records and the default mean the same thing.
        Assert.Equal(Classification.Public, default(Classification));
    }

    [Fact]
    public void CurationType_HasSevenValues()
    {
        Assert.Equal(7, Enum.GetValues<CurationType>().Length);
    }

    [Theory]
    [InlineData(CurationType.Amended)]
    [InlineData(CurationType.Annotated)]
    [InlineData(CurationType.Pinned)]
    [InlineData(CurationType.Demoted)]
    [InlineData(CurationType.Split)]
    [InlineData(CurationType.Merged)]
    [InlineData(CurationType.Reverted)]
    public void CurationType_AllExpectedMembersExist(CurationType type)
    {
        Assert.True(Enum.IsDefined(type));
    }

    [Fact]
    public void WorkstreamMode_DefaultIsAutoDistill()
    {
        Assert.Equal(WorkstreamMode.AutoDistill, default(WorkstreamMode));
    }

    [Fact]
    public void PinScope_DefaultIsWorkstream()
    {
        Assert.Equal(PinScope.Workstream, default(PinScope));
    }

    [Fact]
    public void HypothesisState_AllExpectedMembersExist()
    {
        var values = Enum.GetValues<HypothesisState>();
        Assert.Contains(HypothesisState.Open, values);
        Assert.Contains(HypothesisState.Confirmed, values);
        Assert.Contains(HypothesisState.Refuted, values);
        Assert.Contains(HypothesisState.Abandoned, values);
    }

    [Fact]
    public void GoalState_AllExpectedMembersExist()
    {
        var values = Enum.GetValues<GoalState>();
        Assert.Contains(GoalState.Active, values);
        Assert.Contains(GoalState.Achieved, values);
        Assert.Contains(GoalState.Abandoned, values);
    }

    [Theory]
    [InlineData(OutcomePolarity.Negative, -1)]
    [InlineData(OutcomePolarity.Neutral, 0)]
    [InlineData(OutcomePolarity.Positive, 1)]
    public void OutcomePolarity_HasSignedValues(OutcomePolarity polarity, int expected)
    {
        // Signed values let consumers do arithmetic
        // (sum of polarities = net signal).
        Assert.Equal(expected, (int)polarity);
    }
}
