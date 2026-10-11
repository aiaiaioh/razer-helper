using RazerHelper.Core.Models;

namespace RazerHelper.Core.Services;

/// <summary>What Temp Target is doing to the fans right now: leaving them to the laptop, or holding them at a speed.</summary>
internal readonly record struct FanTargetState(bool Manual, int Rpm)
{
    public static FanTargetState Auto { get; } = new(false, 0);
}

/// <summary>
/// The decision behind Temp Target, with no hardware in it: given the hottest
/// temperature and the target, what should the fans do next. It runs once every
/// few seconds, so every step is small and the rules lean towards cooling:
/// speeding up is quicker than slowing down.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Far below the target the fans stay on Auto, which can run slower than any speed set by hand.</item>
/// <item>Within <see cref="TakeOverBelowTarget"/> degrees of the target the app takes over, at the slowest speed it can hold.</item>
/// <item>Over the target it speeds up (more the further over); under it, it slows down a little at a time.</item>
/// <item>Back at the slowest speed and <see cref="HandBackBelowTarget"/> degrees under, it hands the fans back to Auto.</item>
/// <item>Far over the target, or past <see cref="HardLimitCelsius"/> whatever the target is, it goes flat out.</item>
/// </list>
/// </remarks>
internal static class FanTargetPolicy
{
    public const int MinTargetCelsius = 60;
    public const int MaxTargetCelsius = 90;
    public const int DefaultTargetCelsius = 85;

    /// <summary>At or above this the fans run flat out, even if the target is set higher.</summary>
    public const double HardLimitCelsius = 92;

    public const double TakeOverBelowTarget = 2;
    public const double HandBackBelowTarget = 5;
    public const double FlatOutOverTarget = 5;

    public static int ClampTarget(int celsius) => Math.Clamp(celsius, MinTargetCelsius, MaxTargetCelsius);

    public static FanTargetState Next(FanTargetState state, double hottestCelsius, int targetCelsius, FanRpmRange range)
    {
        var error = hottestCelsius - targetCelsius;

        if (hottestCelsius >= HardLimitCelsius || error >= FlatOutOverTarget)
            return new FanTargetState(true, range.Max);

        if (!state.Manual)
            return error >= -TakeOverBelowTarget ? new FanTargetState(true, range.Min) : state;

        var rpm = state.Rpm;

        if (error > 3)
            rpm += 500;
        else if (error > 1)
            rpm += 200;
        else if (error < -4)
            rpm -= 200;
        else if (error < -1)
            rpm -= 100;

        rpm = range.Clamp(rpm);

        return rpm <= range.Min && error <= -HandBackBelowTarget
            ? FanTargetState.Auto
            : new FanTargetState(true, rpm);
    }
}
