using RazerHelper.Core.Services;

namespace RazerHelper.Tests.Services;

public class RazerDaemonIpcTests
{
    [Fact]
    public void TheShutdownCommand_IsTheLastVariantAsAFourByteLittleEndianNumber() =>
        // bincode: a variant with no fields is its index as a u32. 27 is Shutdown's place in DaemonCommand.
        Assert.Equal(new byte[] { 27, 0, 0, 0 }, RazerDaemonIpc.ShutdownCommand());

    [Fact]
    public void EachCallGetsItsOwnBuffer_SoNoOneCanChangeTheCommandForTheNext()
    {
        var first = RazerDaemonIpc.ShutdownCommand();
        first[0] = 0;

        Assert.Equal(27, RazerDaemonIpc.ShutdownCommand()[0]);
    }

    [Fact]
    public void WithNoDaemonToControl_NothingIsShownAndNothingCanBeChanged()
    {
        var daemon = new NoRazerDaemonControl();

        Assert.Equal(RazerDaemonState.NotInstalled, daemon.GetState());
        Assert.False(daemon.Start().Success);
        Assert.False(daemon.Stop().Success);
    }
}
