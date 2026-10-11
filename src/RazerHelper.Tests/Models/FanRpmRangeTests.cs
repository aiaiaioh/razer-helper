using RazerHelper.Core.Models;

namespace RazerHelper.Tests.Models;

public class FanRpmRangeTests
{
    private static readonly FanRpmRange Range = new(3500, 5000);

    [Theory]
    [InlineData(100, 3500)]
    [InlineData(3500, 3500)]
    [InlineData(4249, 4200)]
    [InlineData(4250, 4300)]
    [InlineData(5000, 5000)]
    [InlineData(9000, 5000)]
    public void ASpeedIsRoundedToTheLaptopsStepsAndKeptInRange(int asked, int expected) =>
        Assert.Equal(expected, Range.Clamp(asked));
}
