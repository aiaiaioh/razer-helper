using RazerHelper.Core.Models;
using RazerHelper.Core.Services;

namespace RazerHelper.Tests.Services;

public class FanTargetPolicyTests
{
    private static readonly FanRpmRange Range = new(3500, 5000);

    [Fact]
    public void FarBelowTheTarget_TheFansStayWithTheLaptop()
    {
        var next = FanTargetPolicy.Next(FanTargetState.Auto, 70, 88, Range);

        Assert.False(next.Manual);
    }

    [Fact]
    public void NearTheTarget_TheAppTakesOverAtTheSlowestSpeed()
    {
        var next = FanTargetPolicy.Next(FanTargetState.Auto, 86, 88, Range);

        Assert.Equal(new FanTargetState(true, 3500), next);
    }

    [Fact]
    public void ALittleOverTheTarget_TheFansSpeedUpALittle()
    {
        var next = FanTargetPolicy.Next(new FanTargetState(true, 3500), 91.5, 90, Range);

        Assert.Equal(new FanTargetState(true, 3700), next);
    }

    [Fact]
    public void WellOverTheTarget_TheFansSpeedUpMuchMore()
    {
        var next = FanTargetPolicy.Next(new FanTargetState(true, 3500), 91.5, 88, Range);

        Assert.Equal(new FanTargetState(true, 4000), next);
    }

    [Fact]
    public void FiveOrMoreOverTheTarget_TheFansGoFlatOut()
    {
        var next = FanTargetPolicy.Next(new FanTargetState(true, 3500), 85, 80, Range);

        Assert.Equal(new FanTargetState(true, 5000), next);
    }

    [Fact]
    public void PastTheHardLimit_TheFansGoFlatOutWhateverTheTarget()
    {
        var next = FanTargetPolicy.Next(FanTargetState.Auto, FanTargetPolicy.HardLimitCelsius, FanTargetPolicy.MaxTargetCelsius, Range);

        Assert.Equal(new FanTargetState(true, 5000), next);
    }

    [Fact]
    public void WithinAWhisperOfTheTarget_TheSpeedIsHeld()
    {
        var held = new FanTargetState(true, 4200);

        Assert.Equal(held, FanTargetPolicy.Next(held, 88.5, 88, Range));
        Assert.Equal(held, FanTargetPolicy.Next(held, 87.5, 88, Range));
    }

    [Fact]
    public void UnderTheTarget_TheFansSlowDownALittleAtATime()
    {
        var next = FanTargetPolicy.Next(new FanTargetState(true, 4200), 85, 88, Range);

        Assert.Equal(new FanTargetState(true, 4100), next);
    }

    [Fact]
    public void WellUnderTheTarget_TheyEaseOffFaster()
    {
        var next = FanTargetPolicy.Next(new FanTargetState(true, 4200), 82, 88, Range);

        Assert.Equal(new FanTargetState(true, 4000), next);
    }

    [Fact]
    public void AtTheSlowestSpeedAndWellUnderTheTarget_TheFansGoBackToAuto()
    {
        var next = FanTargetPolicy.Next(new FanTargetState(true, 3500), 80, 88, Range);

        Assert.False(next.Manual);
    }

    [Fact]
    public void AtTheSlowestSpeedButOnlyALittleUnderTheTarget_TheyStayHeld()
    {
        var next = FanTargetPolicy.Next(new FanTargetState(true, 3500), 85, 88, Range);

        Assert.Equal(new FanTargetState(true, 3500), next);
    }

    [Fact]
    public void SpeedsNeverLeaveTheModelsRange()
    {
        var state = new FanTargetState(true, 4900);

        for (var step = 0; step < 20; step++)
        {
            state = FanTargetPolicy.Next(state, 90.5, 88, Range);
            Assert.InRange(state.Rpm, 3500, 5000);
        }

        Assert.Equal(5000, state.Rpm);
    }

    [Theory]
    [InlineData(30, 60)]
    [InlineData(85, 85)]
    [InlineData(120, 90)]
    public void TheTargetIsKeptInItsRange(int asked, int expected) =>
        Assert.Equal(expected, FanTargetPolicy.ClampTarget(asked));
}
