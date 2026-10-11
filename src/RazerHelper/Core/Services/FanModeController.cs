using System.ComponentModel;
using RazerHelper.Core.Diagnostics;
using RazerHelper.Core.Hardware;
using RazerHelper.Core.Models;

namespace RazerHelper.Core.Services;

internal enum FanMode
{
    /// <summary>The laptop runs the fans.</summary>
    Auto,

    /// <summary>Both fans held flat out.</summary>
    Max,

    /// <summary>The fans are steered to keep the hottest of the CPU and GPU at a target.</summary>
    TempTarget
}

/// <param name="Mode">The mode now in force.</param>
/// <param name="Problem">Why something did not work or stopped on its own, for the header. Null when there is nothing to say.</param>
internal sealed record FanModeChange(FanMode Mode, string? Problem);

/// <summary>
/// Owns which fan mode is on, and for Temp Target runs the loop that holds it.
/// The loop runs on its own timer, only while Temp Target is on: the popup can
/// be closed and it keeps working. Anything that goes wrong in it ends up back
/// on Auto rather than leaving the fans held at a speed nobody is watching.
/// Methods block on the laptop, so call them off the UI thread; <see cref="Changed"/>
/// is raised on whatever thread made the change.
/// </summary>
internal sealed class FanModeController : IDisposable
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(3);

    // This many bad ticks in a row (no temperature, or a failed write) and Temp Target gives up.
    private const int MaxFailures = 3;

    private readonly FanControlService _service;
    private readonly Func<TemperatureReading> _readTemperatures;
    private readonly object _gate = new();

    private System.Threading.Timer? _timer;
    private volatile FanMode _mode = FanMode.Auto;
    private FanTargetState _state = FanTargetState.Auto;
    // Read by the loop and set from the UI without the gate, so a click never waits on the laptop.
    private volatile int _targetCelsius = FanTargetPolicy.DefaultTargetCelsius;
    private int _failures;
    private bool _disposed;

    public FanModeController(FanControlService service, Func<TemperatureReading> readTemperatures)
    {
        _service = service;
        _readTemperatures = readTemperatures;
    }

    public FanMode Mode => _mode;

    public event EventHandler<FanModeChange>? Changed;

    /// <summary>Gives the fans back to the laptop.</summary>
    public void SetAuto()
    {
        FanModeChange change;

        lock (_gate)
        {
            try
            {
                // The loop (if any) keeps running until this has worked.
                _service.SetAuto();
                StopLoop();
                _mode = FanMode.Auto;
                change = new FanModeChange(FanMode.Auto, null);
            }
            catch (Exception exception) when (IsHardwareFailure(exception))
            {
                AppLog.Error("Could not give the fans back to Auto.", exception);
                change = new FanModeChange(_mode, "Could not switch the fans to Auto.");
            }
        }

        Changed?.Invoke(this, change);
    }

    /// <summary>Holds both fans at the model's top speed.</summary>
    public void SetMax()
    {
        FanModeChange change;

        lock (_gate)
        {
            try
            {
                var rpm = _service.SetManual(_service.Range.Max);
                StopLoop();
                _mode = FanMode.Max;
                AppLog.Info($"Fans held at max ({rpm} RPM).");
                change = new FanModeChange(FanMode.Max, null);
            }
            catch (Exception exception) when (IsHardwareFailure(exception))
            {
                AppLog.Error("Could not set the fans to max.", exception);
                change = FailSafe("Could not set the fans to max.");
            }
        }

        Changed?.Invoke(this, change);
    }

    /// <summary>Starts steering the fans to hold <paramref name="celsius"/>. The first adjustment is made at once.</summary>
    public void SetTempTarget(int celsius)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _targetCelsius = FanTargetPolicy.ClampTarget(celsius);

            // Coming from Max the fans are held at full speed, so the loop starts
            // from there and eases off; from Auto it starts by leaving them alone.
            var heldAtMax = _mode == FanMode.Max;

            StopLoop();
            _state = heldAtMax ? new FanTargetState(true, _service.Range.Max) : FanTargetState.Auto;
            _mode = FanMode.TempTarget;
            AppLog.Info($"Temp Target on: {_targetCelsius} degrees Celsius.");

            _timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.FromMilliseconds(200), TickInterval);
        }

        Changed?.Invoke(this, new FanModeChange(FanMode.TempTarget, null));
    }

    /// <summary>Moves the target while Temp Target is on; the next adjustment uses it. Harmless when it is off.</summary>
    public void SetTargetCelsius(int celsius) =>
        _targetCelsius = FanTargetPolicy.ClampTarget(celsius);

    /// <summary>
    /// The performance mode was changed, which puts the fans back on Auto by itself.
    /// Stops anything this was doing and forgets what it held, without writing to the laptop.
    /// </summary>
    public void NoteFansResetByLaptop()
    {
        var changed = false;

        lock (_gate)
        {
            StopLoop();
            _service.Forget();

            if (_mode != FanMode.Auto)
            {
                _mode = FanMode.Auto;
                changed = true;
            }
        }

        if (changed)
            Changed?.Invoke(this, new FanModeChange(FanMode.Auto, null));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            StopLoop();

            // Never leave the fans held at a speed once nothing is watching them.
            if (_mode != FanMode.Auto)
            {
                try
                {
                    _service.SetAuto();
                    AppLog.Info("Fans given back to Auto on exit.");
                }
                catch (Exception exception) when (IsHardwareFailure(exception))
                {
                    AppLog.Error("Could not give the fans back to Auto on exit.", exception);
                }

                _mode = FanMode.Auto;
            }
        }
    }

    private void Tick()
    {
        // A tick that finds the last one still running is skipped, not queued.
        if (!Monitor.TryEnter(_gate))
            return;

        FanModeChange? change = null;

        try
        {
            if (_disposed || _mode != FanMode.TempTarget)
                return;

            change = RunTick();
        }
        finally
        {
            Monitor.Exit(_gate);
        }

        if (change is not null)
            Changed?.Invoke(this, change);
    }

    // Runs with the gate held. Returns a change to report, or null when nothing needs saying.
    private FanModeChange? RunTick()
    {
        var reading = _readTemperatures();
        var hottest = Hottest(reading);

        if (hottest is null)
        {
            // One missed reading holds the fans where they are; several in a row hands them back.
            return ++_failures >= MaxFailures
                ? FailSafe("Temp Target stopped: no temperature readings.")
                : null;
        }

        var next = FanTargetPolicy.Next(_state, hottest.Value, _targetCelsius, _service.Range);

        try
        {
            if (next.Manual)
                _service.SetManual(next.Rpm);
            else if (_state.Manual)
                _service.SetAuto();

            _state = next;
            _failures = 0;
            return null;
        }
        catch (CustomModeFanException)
        {
            // The performance mode was switched to Custom from outside the app.
            AppLog.Info("Temp Target stopped: the laptop went into Custom mode.");
            StopLoop();
            _service.Forget();
            _mode = FanMode.Auto;
            return new FanModeChange(FanMode.Auto, "Temp Target stopped: Custom mode runs its own fans.");
        }
        catch (Exception exception) when (IsHardwareFailure(exception))
        {
            AppLog.Error("Temp Target could not adjust the fans.", exception);

            return ++_failures >= MaxFailures
                ? FailSafe("Temp Target stopped: could not adjust the fans.")
                : null;
        }
    }

    // Stops whatever was running and gives the fans back, as well as it can.
    // Runs with the gate held.
    private FanModeChange FailSafe(string problem)
    {
        StopLoop();

        try
        {
            _service.SetAuto();
        }
        catch (Exception exception) when (IsHardwareFailure(exception))
        {
            AppLog.Error("Could not give the fans back to Auto after a failure.", exception);
            _service.Forget();
        }

        _mode = FanMode.Auto;
        return new FanModeChange(FanMode.Auto, problem);
    }

    private static double? Hottest(TemperatureReading reading)
    {
        if (reading.CpuCelsius is { } cpu)
            return reading.GpuCelsius is { } gpu ? Math.Max(cpu, gpu) : cpu;

        return reading.GpuCelsius;
    }

    private void StopLoop()
    {
        _timer?.Dispose();
        _timer = null;
        _state = FanTargetState.Auto;
        _failures = 0;
    }

    private static bool IsHardwareFailure(Exception exception) =>
        exception is InvalidOperationException or Win32Exception or IOException;
}
