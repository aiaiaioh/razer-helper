namespace RazerHelper.Core.Services;

/// <summary>Where razer-daemon (the background program of razer-control-win) stands.</summary>
internal enum RazerDaemonState
{
    /// <summary>It is not running and nothing on this PC says it is installed, so the controls stay hidden.</summary>
    NotInstalled,

    /// <summary>Installed but not running.</summary>
    Stopped,

    Running
}

/// <param name="Success">The daemon is now in the state that was asked for.</param>
/// <param name="Message">What went wrong, in words for the header. Empty on success.</param>
/// <param name="Cancelled">The user declined the Windows administrator prompt, which is not a failure worth a red message.</param>
internal sealed record DaemonActionResult(bool Success, string Message = "", bool Cancelled = false)
{
    public static DaemonActionResult Ok { get; } = new(true);

    public static DaemonActionResult Declined { get; } = new(false, Cancelled: true);

    public static DaemonActionResult Failed(string message) => new(false, message);
}

/// <summary>Starts and stops razer-daemon. Every member may block, so call them off the UI thread.</summary>
internal interface IRazerDaemonControl
{
    RazerDaemonState GetState();

    DaemonActionResult Start();

    DaemonActionResult Stop();
}

/// <summary>Used by tests and previews: no daemon, so the controls never appear and nothing on the machine is touched.</summary>
internal sealed class NoRazerDaemonControl : IRazerDaemonControl
{
    public RazerDaemonState GetState() => RazerDaemonState.NotInstalled;

    public DaemonActionResult Start() => DaemonActionResult.Failed("No razer-daemon.");

    public DaemonActionResult Stop() => DaemonActionResult.Failed("No razer-daemon.");
}
