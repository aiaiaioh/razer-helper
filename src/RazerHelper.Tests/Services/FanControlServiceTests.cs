using RazerHelper.Core.Hardware;
using RazerHelper.Core.Models;
using RazerHelper.Core.Services;
using RazerHelper.Tests.TestSupport;

namespace RazerHelper.Tests.Services;

public class FanControlServiceTests
{
    private readonly FakeEc _ec = new();
    private readonly FanControlService _service;

    public FanControlServiceTests() =>
        _service = new FanControlService(_ec, () => new FanRpmRange(3500, 5000));

    [Fact]
    public void HoldingASpeed_PutsBothFansInManualAndSetsTheSpeed()
    {
        var used = _service.SetManual(5000);

        Assert.Equal(5000, used);
        Assert.Equal([1, 1], _ec.FanManual);
        Assert.Equal([50, 50], _ec.FanSetpointHundreds);
    }

    [Fact]
    public void ASpeedOutsideTheRangeIsBroughtInside()
    {
        Assert.Equal(3500, _service.SetManual(1000));
        Assert.Equal([35, 35], _ec.FanSetpointHundreds);
    }

    [Fact]
    public void HoldingASpeed_KeepsThePerformanceModeAsItWas()
    {
        _ec.SetBothZones((byte)PerformanceMode.Silent);

        _service.SetManual(4000);

        Assert.Equal([(byte)PerformanceMode.Silent, (byte)PerformanceMode.Silent], _ec.ZoneMode);
    }

    [Fact]
    public void AskingForTheSameSpeedAgain_WritesNothing()
    {
        _service.SetManual(4000);
        var writesBefore = _ec.Writes.Count();

        _service.SetManual(4000);

        Assert.Equal(writesBefore, _ec.Writes.Count());
    }

    [Fact]
    public void ChangingTheSpeed_WritesOnlyTheSpeeds()
    {
        _service.SetManual(4000);
        _ec.Log.Clear();

        _service.SetManual(4500);

        Assert.All(_ec.Writes, write => Assert.Equal(RazerCommands.SetFanRpm, write.Command));
        Assert.Equal(2, _ec.Writes.Count());
    }

    [Fact]
    public void GivingTheFansBack_ClearsManualAndKeepsTheMode()
    {
        _service.SetManual(4000);

        _service.SetAuto();

        Assert.Equal([0, 0], _ec.FanManual);
        Assert.Equal([0, 0], _ec.ZoneMode);
    }

    [Fact]
    public void AfterGivingTheFansBack_HoldingASpeedAgainWritesEverything()
    {
        _service.SetManual(4000);
        _service.SetAuto();

        _service.SetManual(4000);

        Assert.Equal([1, 1], _ec.FanManual);
        Assert.Equal([40, 40], _ec.FanSetpointHundreds);
    }

    [Fact]
    public void InCustomMode_NothingIsWrittenAndItSaysWhy()
    {
        _ec.SetBothZones((byte)PerformanceMode.Custom);

        Assert.Throws<CustomModeFanException>(() => _service.SetManual(4000));
        Assert.Empty(_ec.Writes);
    }

    [Fact]
    public void GivingTheFansBackInCustomMode_WritesNothing()
    {
        _ec.SetBothZones((byte)PerformanceMode.Custom);

        _service.SetAuto();

        Assert.Empty(_ec.Writes);
    }

    [Fact]
    public void WhenTheModeChangedUnderIt_TheFansAreHeldAgainInTheNewMode()
    {
        _service.SetManual(4000);
        _ec.SetBothZones((byte)PerformanceMode.Silent); // Fn+P: the laptop drops back to Auto fans.
        _ec.FanManual[0] = 0;
        _ec.FanManual[1] = 0;

        _service.SetManual(4000);

        Assert.Equal([1, 1], _ec.FanManual);
        Assert.Equal([(byte)PerformanceMode.Silent, (byte)PerformanceMode.Silent], _ec.ZoneMode);
    }

    [Fact]
    public void WhenTheLaptopOnlyConfirmsTheOtherFlag_ThatOneIsUsed()
    {
        var firstArguments = new List<byte>();

        var transport = new ScriptedTransport((command, arguments) =>
        {
            if (command != RazerCommands.SetPerformanceMode)
                return _ec.Send(command, arguments);

            firstArguments.Add(arguments[0]);

            // Echoes the arguments only when the first one is 0.
            return arguments[0] == 0
                ? _ec.Send(command, arguments)
                : FakeEc.Respond(0x00, arguments[1], arguments[2], arguments[3]);
        });

        var service = new FanControlService(transport, () => new FanRpmRange(3500, 5000));

        service.SetManual(4000);

        Assert.Equal([1, 0, 0], firstArguments);
        Assert.Equal([1, 1], _ec.FanManual);
    }
}
