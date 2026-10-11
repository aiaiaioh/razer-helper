using RazerHelper.Core.Models;
using RazerHelper.Core.Services;
using RazerHelper.Tests.TestSupport;

namespace RazerHelper.Tests.Services;

public class FanModeControllerTests
{
    private readonly FakeEc _ec = new();
    private TemperatureReading _temperatures = new(70, 60);

    private FanModeController CreateController() =>
        new(new FanControlService(_ec, () => new FanRpmRange(3500, 5000)), () => _temperatures);

    private static void WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(6);

        while (!condition() && DateTime.UtcNow < deadline)
            Thread.Sleep(20);

        Assert.True(condition(), "Timed out waiting for the fans to change.");
    }

    [Fact]
    public void ItStartsOnAuto() =>
        Assert.Equal(FanMode.Auto, CreateController().Mode);

    [Fact]
    public void Max_HoldsBothFansAtTheTopSpeed()
    {
        using var controller = CreateController();

        controller.SetMax();

        Assert.Equal(FanMode.Max, controller.Mode);
        Assert.Equal([50, 50], _ec.FanSetpointHundreds);
    }

    [Fact]
    public void Auto_GivesTheFansBackAfterMax()
    {
        using var controller = CreateController();
        controller.SetMax();

        controller.SetAuto();

        Assert.Equal(FanMode.Auto, controller.Mode);
        Assert.Equal([0, 0], _ec.FanManual);
    }

    [Fact]
    public void Max_InCustomMode_FailsBackToAutoAndSaysSo()
    {
        _ec.SetBothZones((byte)PerformanceMode.Custom);
        using var controller = CreateController();
        FanModeChange? change = null;
        controller.Changed += (_, c) => change = c;

        controller.SetMax();

        Assert.Equal(FanMode.Auto, controller.Mode);
        Assert.NotNull(change?.Problem);
    }

    [Fact]
    public void TempTarget_TakesOverWhenTheCpuIsNearTheTarget()
    {
        _temperatures = new TemperatureReading(84, 60);
        using var controller = CreateController();

        controller.SetTempTarget(85);

        WaitFor(() => _ec.FanManual[0] == 1 && _ec.FanManual[1] == 1);
        Assert.Equal(FanMode.TempTarget, controller.Mode);
    }

    [Fact]
    public void TempTarget_FollowsTheHotterOfTheTwo()
    {
        _temperatures = new TemperatureReading(60, 94); // The GPU is the hot one, and past the hard limit.
        using var controller = CreateController();

        controller.SetTempTarget(85);

        WaitFor(() => _ec.FanSetpointHundreds[0] == 50);
    }

    [Fact]
    public void TempTarget_LeavesTheFansAloneWhileEverythingIsCool()
    {
        _temperatures = new TemperatureReading(50, 45);
        using var controller = CreateController();

        controller.SetTempTarget(85);
        Thread.Sleep(600);

        Assert.Equal([0, 0], _ec.FanManual);
        Assert.Equal(FanMode.TempTarget, controller.Mode);
    }

    [Fact]
    public void WhenThePerformanceModeChanges_ItStopsHoldingAndForgets()
    {
        using var controller = CreateController();
        controller.SetMax();

        controller.NoteFansResetByLaptop();

        Assert.Equal(FanMode.Auto, controller.Mode);
    }

    [Fact]
    public void DisposingGivesTheFansBack()
    {
        var controller = CreateController();
        controller.SetMax();

        controller.Dispose();

        Assert.Equal([0, 0], _ec.FanManual);
    }

    [Fact]
    public void TheTargetIsKeptInItsRange()
    {
        using var controller = CreateController();
        _temperatures = new TemperatureReading(91, 60);

        // Asked for 500 degrees: if that were used the fans would stay on Auto.
        // Kept to the top of the range (90), 91 is over it and the app takes over.
        controller.SetTempTarget(500);

        WaitFor(() => _ec.FanManual[0] == 1);
    }
}
