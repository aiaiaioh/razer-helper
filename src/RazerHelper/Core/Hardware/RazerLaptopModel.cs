using RazerHelper.Core.Models;

namespace RazerHelper.Core.Hardware;

/// <summary>One Razer laptop this app can look for, by its USB product id.</summary>
/// <param name="Verified">
/// Someone has confirmed the commands in <see cref="RazerCommands"/> actually
/// work on this model. An unverified entry is still tried the same way: a
/// command the firmware does not implement fails cleanly with
/// <see cref="RazerCommandNotSupportedException"/> instead of doing something
/// unexpected.
/// </param>
/// <param name="MinFanRpm">The slowest speed the fans can be held at by hand (below it only Auto runs them).</param>
/// <param name="MaxFanRpm">The fastest speed, used by Max.</param>
internal sealed record RazerLaptopModel(
    int ProductId,
    string Name,
    bool Verified,
    int MinFanRpm = 3500,
    int MaxFanRpm = 5000)
{
    public FanRpmRange FanRange => new(MinFanRpm, MaxFanRpm);
}
