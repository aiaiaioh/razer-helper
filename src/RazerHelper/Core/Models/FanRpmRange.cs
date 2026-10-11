namespace RazerHelper.Core.Models;

/// <summary>
/// The slowest and fastest a fan can be held at by hand, in RPM. Below the
/// minimum only the laptop's own automatic control can run the fans, which is
/// why Temp Target hands back to Auto when there is room to.
/// </summary>
internal readonly record struct FanRpmRange(int Min, int Max)
{
    /// <summary>The fan speed is written to the laptop in hundreds of RPM.</summary>
    public const int Step = 100;

    public static FanRpmRange Default { get; } = new(3500, 5000);

    /// <summary>The nearest speed the laptop can be given: a multiple of 100, no lower than Min and no higher than Max.</summary>
    public int Clamp(int rpm)
    {
        var stepped = (int)Math.Round(rpm / (double)Step, MidpointRounding.AwayFromZero) * Step;
        return Math.Clamp(stepped, Min, Max);
    }
}
