using RazerHelper.Core.Diagnostics;
using RazerHelper.Core.Hardware;
using RazerHelper.Core.Models;

namespace RazerHelper.Core.Services;

/// <summary>
/// Holds both fans at a chosen speed, or gives them back to the laptop's own
/// control. The laptop keeps a performance mode per zone (CPU and GPU) and
/// each mode write carries a fan byte: 0 lets the laptop run that zone's fan,
/// 1 hands it to a speed set with <see cref="RazerCommands.SetFanRpm"/>. So
/// holding a speed means re-writing the zone's current mode with the fan byte
/// set, then writing the speed; going back to Auto re-writes the same mode
/// with it cleared. The performance mode itself is never changed here.
/// </summary>
/// <remarks>
/// Custom mode is left alone: the laptop runs its fans itself there (razer-control-win's
/// daemon documents the same), and its own Max flag (see <see cref="PerformanceService"/>)
/// is the way to get full speed. Not thread-safe; <see cref="FanModeController"/>
/// is the only caller and serialises its use.
/// </remarks>
internal sealed class FanControlService(IRazerTransport transport, Func<FanRpmRange> range)
{
    private const byte FanByteAuto = 0x00;
    private const byte FanByteManual = 0x01;
    private const byte CpuZone = 0x01;
    private const byte GpuZone = 0x02;

    private static readonly byte[] Zones = [CpuZone, GpuZone];

    // The first argument of a performance-mode write. The Blade 16 takes 1;
    // razer-control-win's daemon sends 0 on the Blade 14. If a write is not
    // confirmed with 1, 0 is tried once, and whichever works is kept.
    private byte _writeFlag = 0x01;
    private bool _writeFlagSettled;

    // What is currently held by hand: each zone's mode at the time, and the speed.
    private byte[]? _heldModes;
    private int _heldRpm;

    public FanRpmRange Range => range();

    /// <summary>
    /// Holds both fans at <paramref name="rpm"/> (rounded to the laptop's steps and kept
    /// within its range) and returns the speed used. Writes only what changed since last time.
    /// </summary>
    /// <exception cref="CustomModeFanException">The laptop is in Custom mode.</exception>
    /// <exception cref="InvalidOperationException">The laptop did not confirm a write.</exception>
    public int SetManual(int rpm)
    {
        var speed = range().Clamp(rpm);
        var modes = ReadModes();

        if (modes.Contains((byte)PerformanceMode.Custom))
            throw new CustomModeFanException();

        var previousModes = _heldModes;

        // A failure part-way must make the next call write everything again.
        _heldModes = null;

        for (var index = 0; index < Zones.Length; index++)
        {
            var enteringManual = previousModes is null || previousModes[index] != modes[index];

            if (enteringManual)
                WriteZone(Zones[index], modes[index], FanByteManual);

            if (enteringManual || _heldRpm != speed)
                transport.Send(RazerCommands.SetFanRpm, [0x00, Zones[index], (byte)(speed / FanRpmRange.Step)]);
        }

        _heldModes = modes;
        _heldRpm = speed;
        return speed;
    }

    /// <summary>Gives both fans back to the laptop. Does nothing to a zone in Custom mode, where the laptop already runs them.</summary>
    public void SetAuto()
    {
        Forget();

        foreach (var zone in Zones)
        {
            var mode = ReadZoneMode(zone);

            if (mode != (byte)PerformanceMode.Custom)
                WriteZone(zone, mode, FanByteAuto);
        }
    }

    /// <summary>
    /// Drops what this service believes is held, without writing anything. For
    /// when something else (a performance mode change) has already put the fans back on Auto.
    /// </summary>
    public void Forget()
    {
        _heldModes = null;
        _heldRpm = 0;
    }

    private byte[] ReadModes() => [.. Zones.Select(ReadZoneMode)];

    private byte ReadZoneMode(byte zone)
    {
        var response = transport.Send(RazerCommands.GetPerformanceMode, [0x00, zone, 0x00, 0x00]);

        if (RazerHidPacket.GetArgument(response, 1) != zone)
            throw new InvalidOperationException("The performance-mode response used an unexpected zone.");

        return RazerHidPacket.GetArgument(response, 2);
    }

    private void WriteZone(byte zone, byte mode, byte fanByte)
    {
        var description = $"fan control {(fanByte == FanByteManual ? "by hand" : "back to Auto")} for zone {zone}";

        try
        {
            transport.SendAndConfirm(RazerCommands.SetPerformanceMode, [_writeFlag, zone, mode, fanByte], description);
            _writeFlagSettled = true;
        }
        catch (InvalidOperationException exception) when (
            exception is not RazerCommandNotSupportedException && !_writeFlagSettled && _writeFlag == 0x01)
        {
            AppLog.Info("The laptop did not confirm the fan write with flag 1; trying flag 0.");
            _writeFlag = 0x00;

            transport.SendAndConfirm(RazerCommands.SetPerformanceMode, [_writeFlag, zone, mode, fanByte], description);
            _writeFlagSettled = true;
        }
    }
}

/// <summary>The laptop is in Custom mode, where it runs its own fans and a speed cannot be held by hand.</summary>
internal sealed class CustomModeFanException()
    : InvalidOperationException("Manual fan speed is not available in Custom mode.");
