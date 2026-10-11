using RazerHelper.Core.Diagnostics;
using RazerHelper.Core.Hardware;
using RazerHelper.Core.Models;
using RazerHelper.Core.Services;
using static RazerHelper.UI.UiControls;
using static RazerHelper.UI.UiTheme;

namespace RazerHelper.UI.Sections;

/// <summary>
/// CPU and GPU fan speed readout, and the fan mode: Auto (the laptop runs the
/// fans), Max (both flat out) or Temp Target (the app steers the fans to hold
/// the hottest of the CPU and GPU at a chosen temperature). Only the mode in
/// force is lit. Every poll it also reads the CPU and GPU temperatures and
/// announces them (<see cref="TemperaturesRead"/>), for the host to show wherever
/// it likes. The readout polls only while the host says the popup is visible;
/// Temp Target has its own timer that runs only while that mode is on.
/// </summary>
internal sealed class FanSection : SectionPanel
{
    private const int PollIntervalMilliseconds = 2_000;

    // Each Auto / Max button plus its margins; the Temp Target group is wider.
    private static int ModeButtonCellWidth => S(140);
    private static int TargetCellWidth => S(176);
    private static int StepperColumnWidth => S(40);

    private const string NoDeviceHint = "Needs a supported Razer Blade";
    private const string MaxNeedsPowerHint = "Needs plugged in";
    private const string TargetNotInCustomHint = "Not available in Custom mode";

    private readonly FanTelemetryService _telemetryService;
    private readonly FanModeController _controller;
    private readonly ICpuTemperatureSource _cpuTemperature;
    private readonly IGpuTemperatureSource _gpuTemperature;
    private readonly HashSet<string> _loggedTemperatureFailures = [];
    private readonly IPowerSource _powerSource;
    private readonly ThemedToolTip _toolTip = new();
    private readonly System.Windows.Forms.Timer _pollTimer = new()
    {
        Interval = PollIntervalMilliseconds
    };
    private readonly Label _cpuFanLabel;
    private readonly Label _gpuFanLabel;
    private readonly Button[] _modeButtons;
    private readonly Button _autoButton;
    private readonly Button _maxButton;
    private readonly Button _targetButton;
    private readonly Button _targetUpButton;
    private readonly Button _targetDownButton;

    private PerformanceState _performanceState = PerformanceState.Unknown;
    private PerformanceMode? _lastKnownMode;
    private int _targetCelsius;
    private bool _isMaxAvailable;
    private bool _isTargetAvailable;
    private bool _changing;
    private bool _isPolling;
    private bool _refreshInProgress;

    public FanSection(
        FanTelemetryService telemetryService,
        FanControlService fanControl,
        IPowerSource powerSource,
        ICpuTemperatureSource cpuTemperature,
        IGpuTemperatureSource gpuTemperature,
        int? savedTargetCelsius)
    {
        _telemetryService = telemetryService;
        _powerSource = powerSource;
        _cpuTemperature = cpuTemperature;
        _gpuTemperature = gpuTemperature;

        _targetCelsius = FanTargetPolicy.ClampTarget(savedTargetCelsius ?? FanTargetPolicy.DefaultTargetCelsius);

        _controller = new FanModeController(fanControl, ReadTemperatures);
        _controller.SetTargetCelsius(_targetCelsius);
        _controller.Changed += Controller_Changed;

        _cpuFanLabel = CreateReadingLabel("CPU Fan: -- RPM");
        _gpuFanLabel = CreateReadingLabel("GPU Fan: -- RPM");

        // Each reading sits above its own button: CPU over Auto, GPU over Max,
        // side by side, and the Temp Target caption over its box. The columns
        // are the same width as the cells below, so the text lines up with the
        // left edge of each button.
        var readings = new TableLayoutPanel
        {
            BackColor = BackgroundColor,
            ColumnCount = 3,
            Dock = DockStyle.Top,
            Height = S(24),
            Margin = Padding.Empty,
            Padding = S(new Padding(0, 0, 0, 2)),
            RowCount = 1
        };

        readings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ModeButtonCellWidth));
        readings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ModeButtonCellWidth));
        readings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        readings.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        readings.Controls.Add(_cpuFanLabel, 0, 0);
        readings.Controls.Add(_gpuFanLabel, 1, 0);

        var targetCaption = CreateReadingLabel("Temp Target");
        targetCaption.Font = GetDesignFont("Segoe UI", 9F, FontStyle.Bold);
        targetCaption.ForeColor = Color.White;
        readings.Controls.Add(targetCaption, 2, 0);

        // Auto | Max | Temp Target. Synapse's "Max Fan Speed Mode" had the first two.
        _autoButton = CreateModeButton("Auto");
        _maxButton = CreateModeButton("Max");

        // The Temp Target box shows the target; clicking it turns the mode on. The
        // small arrows beside it change the target by a degree and never switch
        // the mode on or off themselves.
        _targetButton = CreateActionButton(TargetText(_targetCelsius));
        _targetButton.Name = "TargetFanModeButton";
        _targetButton.Tag = "Temp Target";
        _targetButton.Margin = S(new Padding(4, 4, 0, 4));

        _targetUpButton = CreateStepButton("\u25B2", "TargetUpButton", new Padding(0, 0, 0, 1));
        _targetDownButton = CreateStepButton("\u25BC", "TargetDownButton", new Padding(0, 1, 0, 0));

        _modeButtons = [_autoButton, _maxButton, _targetButton];

        var steppers = new TableLayoutPanel
        {
            BackColor = BackgroundColor,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Margin = S(new Padding(0, 4, 4, 4)),
            Padding = Padding.Empty,
            RowCount = 2
        };

        steppers.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        steppers.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
        steppers.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
        steppers.Controls.Add(_targetUpButton, 0, 0);
        steppers.Controls.Add(_targetDownButton, 0, 1);

        var targetCell = new TableLayoutPanel
        {
            BackColor = BackgroundColor,
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            RowCount = 1
        };

        targetCell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        targetCell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, StepperColumnWidth));
        targetCell.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        targetCell.Controls.Add(_targetButton, 0, 0);
        targetCell.Controls.Add(steppers, 1, 0);

        var modeGrid = new TableLayoutPanel
        {
            BackColor = BackgroundColor,
            ColumnCount = 3,
            Dock = DockStyle.Left, // Compact rather than full width, left-aligned like the readout above.
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            RowCount = 1,
            Width = ModeButtonCellWidth * 2 + TargetCellWidth
        };

        modeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ModeButtonCellWidth));
        modeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ModeButtonCellWidth));
        modeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, TargetCellWidth));
        modeGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        modeGrid.Controls.Add(_autoButton, 0, 0);
        modeGrid.Controls.Add(_maxButton, 1, 0);
        modeGrid.Controls.Add(targetCell, 2, 0);

        _autoButton.Click += (_, _) => RequestAuto();
        _maxButton.Click += (_, _) => RequestMax();
        _targetButton.Click += (_, _) => RequestTempTarget();
        _targetUpButton.Click += (_, _) => ChangeTarget(+1);
        _targetDownButton.Click += (_, _) => ChangeTarget(-1);
        UpdateModeButtons();

        // Dock order: the header docks first, then the readings, and the mode
        // buttons fill what is left.
        Controls.Add(modeGrid);
        Controls.Add(readings);
        Controls.Add(CreateSectionHeader("Fans", string.Empty));

        _pollTimer.Tick += PollTimer_Tick;
        _powerSource.PowerSourceChanged += PowerSource_PowerSourceChanged;
    }

    /// <summary>
    /// Raised when the user asks for max fan speed on (true) or off (false) while the
    /// laptop is in Custom mode, where the laptop's own max flag does it. The host performs it.
    /// Outside Custom this section holds the fans itself.
    /// </summary>
    public event EventHandler<bool>? MaxFanRequested;

    /// <summary>Raised after every poll with the temperatures read, each null when there is no reading for it.</summary>
    public event EventHandler<TemperatureReading>? TemperaturesRead;

    /// <summary>Raised when something failed, or Temp Target stopped on its own, with the words for the header.</summary>
    public event EventHandler<SectionStatus>? StatusChanged;

    /// <summary>Raised when the user changes the Temp Target number, so the host can keep it.</summary>
    public event EventHandler<int>? TargetChanged;

    /// <summary>Tells the fan buttons which performance mode the laptop is in, since the fan modes depend on it.</summary>
    public void ShowPerformanceState(PerformanceState state)
    {
        _performanceState = state;

        // A performance mode change (from here, Fn+P, or a profile switch) puts the
        // fans back on Auto in the laptop; the app must stop holding them.
        if (state.Mode is { } mode)
        {
            if (_lastKnownMode is { } previous && previous != mode && _controller.Mode != FanMode.Auto)
                _ = Task.Run(_controller.NoteFansResetByLaptop);

            _lastKnownMode = mode;
        }

        UpdateModeButtons();
    }

    /// <summary>Starts polling and takes an immediate reading.</summary>
    public void StartPolling()
    {
        _isPolling = true;
        _pollTimer.Start();
        _ = RefreshAsync();
    }

    public void StopPolling()
    {
        _isPolling = false;
        _pollTimer.Stop();

        // A reading only means something for as long as it is being refreshed.
        // Clearing it now means reopening the popup never shows a temperature
        // from minutes ago while the first fresh one is on its way.
        TemperaturesRead?.Invoke(this, TemperatureReading.None);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pollTimer.Stop();
            _pollTimer.Tick -= PollTimer_Tick;
            _pollTimer.Dispose();
            _powerSource.PowerSourceChanged -= PowerSource_PowerSourceChanged;

            // Stops Temp Target and gives the fans back to the laptop.
            _controller.Changed -= Controller_Changed;
            _controller.Dispose();

            _toolTip.Dispose();
        }

        base.Dispose(disposing);
    }

    private void PowerSource_PowerSourceChanged(object? sender, EventArgs e) =>
        PostToUi(UpdateModeButtons);

    private void Controller_Changed(object? sender, FanModeChange change) =>
        PostToUi(() =>
        {
            UpdateModeButtons();

            if (change.Problem is not null)
                StatusChanged?.Invoke(this, new SectionStatus(change.Problem, IsError: true));
        });

    // The mode in force. In Custom the laptop's own max flag is the truth
    // (it can be changed from outside the app); elsewhere it is whatever the
    // controller holds.
    private FanMode CurrentMode() =>
        _performanceState.Mode == PerformanceMode.Custom
            ? (_performanceState.MaxFan == true ? FanMode.Max : FanMode.Auto)
            : _controller.Mode;

    // Lights the mode in force and greys the rest, and works out which modes can be
    // chosen. A mode that cannot be (Max unplugged in Custom, Temp Target in Custom, no
    // laptop) is drawn like a disabled one and hovering it says why. It is deliberately
    // still enabled underneath, because WinForms shows no tooltip on a disabled control,
    // so the click handlers must refuse the click themselves.
    private void UpdateModeButtons()
    {
        var pluggedIn = PowerProfileRules.TreatAsPluggedIn(_powerSource.IsPluggedIn);
        var known = _performanceState.Mode is not null;
        var inCustom = _performanceState.Mode == PerformanceMode.Custom;
        var mode = CurrentMode();

        _isMaxAvailable = known && (!inCustom || PowerProfileRules.CanUseMaxFan(_performanceState, pluggedIn));
        _isTargetAvailable = known && !inCustom;

        // Highlighting resets the text colors, so the unavailable look goes on after it.
        HighlightSelected(
            _modeButtons,
            mode switch
            {
                FanMode.Max => _maxButton,
                FanMode.TempTarget => _targetButton,
                _ => _autoButton
            });

        SetAvailability(_maxButton, _isMaxAvailable, _toolTip, known ? MaxNeedsPowerHint : NoDeviceHint);
        SetAvailability(_targetButton, _isTargetAvailable, _toolTip, known ? TargetNotInCustomHint : NoDeviceHint);

        // The arrows belong to the Temp Target box: green with it when it is on.
        var arrowColor = mode == FanMode.TempTarget ? RazerGreen : Color.Silver;
        _targetUpButton.ForeColor = arrowColor;
        _targetDownButton.ForeColor = arrowColor;
    }

    private void RequestAuto()
    {
        // Custom: the laptop runs its own fans, apart from its max flag.
        if (_performanceState.Mode == PerformanceMode.Custom)
        {
            if (_performanceState.MaxFan == true)
                MaxFanRequested?.Invoke(this, false);

            return;
        }

        if (_controller.Mode != FanMode.Auto)
            Change(controller => controller.SetAuto());
    }

    private void RequestMax()
    {
        if (!_isMaxAvailable)
            return;

        if (_performanceState.Mode == PerformanceMode.Custom)
        {
            if (_performanceState.MaxFan != true)
                MaxFanRequested?.Invoke(this, true);

            return;
        }

        if (_controller.Mode != FanMode.Max)
            Change(controller => controller.SetMax());
    }

    private void RequestTempTarget()
    {
        if (!_isTargetAvailable || _controller.Mode == FanMode.TempTarget)
            return;

        var target = _targetCelsius;
        Change(controller => controller.SetTempTarget(target));
    }

    private void ChangeTarget(int delta)
    {
        var target = FanTargetPolicy.ClampTarget(_targetCelsius + delta);

        if (target == _targetCelsius)
            return;

        _targetCelsius = target;
        _targetButton.Text = TargetText(target);

        // Takes effect at the loop's next adjustment when Temp Target is on.
        _controller.SetTargetCelsius(target);
        TargetChanged?.Invoke(this, target);
    }

    // One change at a time: a second click while the laptop is still being talked to is ignored.
    private async void Change(Action<FanModeController> change)
    {
        if (_changing)
            return;

        _changing = true;

        try
        {
            await Task.Run(() => change(_controller));
        }
        catch (Exception exception)
        {
            AppLog.Error("Could not change the fan mode.", exception);
            StatusChanged?.Invoke(this, new SectionStatus("Could not change the fan mode.", IsError: true));
        }
        finally
        {
            _changing = false;
            UpdateModeButtons();
        }
    }

    private async void PollTimer_Tick(object? sender, EventArgs e) =>
        await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_refreshInProgress)
            return;

        _refreshInProgress = true;

        try
        {
            // Read together, so the temperatures never trail the fan speeds.
            var temperatures = Task.Run(ReadTemperatures);
            var reading = await _telemetryService.ReadAsync();
            var temperature = await temperatures;

            if (!_isPolling)
                return;

            TemperaturesRead?.Invoke(this, temperature);

            if (reading is null)
                return;

            ShowReading(_cpuFanLabel, "CPU Fan", reading.CpuFanRpm);
            ShowReading(_gpuFanLabel, "GPU Fan", reading.GpuFanRpm);
        }
        catch (Exception exception)
        {
            AppLog.Error("Fan telemetry read failed unexpectedly.", exception);

            if (_isPolling)
            {
                ShowReading(_cpuFanLabel, "CPU Fan", null);
                ShowReading(_gpuFanLabel, "GPU Fan", null);
                TemperaturesRead?.Invoke(this, TemperatureReading.None);
            }
        }
        finally
        {
            _refreshInProgress = false;
        }
    }

    // Each source has its own safety net: a temperature that cannot be read is
    // just missing, and can never take the fan speeds (or the other
    // temperature) down with it. Also called by the Temp Target loop, from
    // its own thread.
    private TemperatureReading ReadTemperatures() => new(
        SafeRead("CPU temperature", _cpuTemperature.ReadCelsius),
        SafeRead("GPU temperature", _gpuTemperature.ReadCelsius));

    private double? SafeRead(string name, Func<double?> read)
    {
        try
        {
            return read();
        }
        catch (Exception exception)
        {
            // Polled every couple of seconds: log a failing source once, not every time.
            bool firstFailure;

            lock (_loggedTemperatureFailures)
                firstFailure = _loggedTemperatureFailures.Add(name);

            if (firstFailure)
                AppLog.Error($"Could not read the {name}.", exception);

            return null;
        }
    }

    private static void ShowReading(Label label, string name, int? rpm) =>
        label.Text = rpm is null ? $"{name}: -- RPM" : $"{name}: {rpm} RPM";

    private static string TargetText(int celsius) => $"{celsius}\u00B0C";

    private static Button CreateModeButton(string name)
    {
        var button = CreateActionButton(name);
        button.Name = $"{name}FanModeButton";
        button.Tag = name;
        return button;
    }

    private static Button CreateStepButton(string glyph, string name, Padding margin)
    {
        var button = CreateActionButton(glyph);
        button.Name = name;
        button.Font = GetDesignFont("Segoe UI Symbol", 7F);
        button.ForeColor = Color.Silver;
        button.Margin = S(margin);
        return button;
    }

    private static Label CreateReadingLabel(string text) => new()
    {
        AutoSize = false,
        Dock = DockStyle.Fill,
        Font = GetDesignFont("Segoe UI", 9.5F),
        ForeColor = Color.Silver,
        Margin = S(new Padding(4, 0, 0, 0)), // Same 4px inset as the buttons below.
        Text = text,
        TextAlign = ContentAlignment.MiddleLeft
    };
}
