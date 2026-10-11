using RazerHelper.Core.Diagnostics;
using RazerHelper.Core.Services;
using static RazerHelper.UI.UiControls;
using static RazerHelper.UI.UiTheme;

namespace RazerHelper.UI.Sections;

/// <summary>
/// The "razer daemon" On / Off pair in the header: starts and stops
/// razer-daemon, the background program of razer-control-win. It only shows
/// when that program is installed or running, so on a PC without it the
/// header looks as it always did. Which button is lit says whether the daemon
/// is running; it is read when the popup opens and after each press, never on
/// a timer.
/// </summary>
internal sealed class DaemonSection : SectionPanel
{
    private static readonly Color StoppedOutlineColor = Color.Gainsboro;

    private readonly IRazerDaemonControl _daemon;
    private readonly ThemedToolTip _toolTip = new();
    private readonly Label _label;
    private readonly Button _onButton;
    private readonly Button _offButton;

    private RazerDaemonState _state = RazerDaemonState.NotInstalled;
    private bool _busy;
    private bool _refreshing;
    private bool _refreshAgain;

    public DaemonSection(IRazerDaemonControl daemon)
    {
        _daemon = daemon;

        // Part of the header, so no gap below; a little room to its left. Hidden until a daemon is found.
        Margin = S(new Padding(16, 0, 0, 0));
        Visible = false;

        _label = new Label
        {
            Dock = DockStyle.Fill,
            Font = GetDesignFont("Segoe UI", 8F),
            ForeColor = SubtleTextColor,
            Text = "razer daemon",
            TextAlign = ContentAlignment.BottomCenter
        };

        _onButton = CreateDaemonButton("On", "DaemonOn");
        _onButton.Click += async (_, _) => await ChangeAsync(start: true);

        _offButton = CreateDaemonButton("Off", "DaemonOff");
        _offButton.Click += async (_, _) => await ChangeAsync(start: false);

        var buttons = new TableLayoutPanel
        {
            BackColor = BackgroundColor,
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            RowCount = 1
        };

        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        buttons.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        buttons.Controls.Add(_onButton, 0, 0);
        buttons.Controls.Add(_offButton, 1, 0);

        var layout = new TableLayoutPanel
        {
            BackColor = BackgroundColor,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            RowCount = 2
        };

        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, S(20)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.Controls.Add(_label, 0, 0);
        layout.Controls.Add(buttons, 0, 1);

        Controls.Add(layout);

        _toolTip.SetToolTip(_label, "razer-daemon, the background program of razer-control-win");
    }

    /// <summary>Raised when the daemon is found or no longer found, so the host can make room for this control or take it back.</summary>
    public event EventHandler<bool>? InstalledChanged;

    public event EventHandler<SectionStatus>? StatusChanged;

    /// <summary>
    /// Raised while the Windows administrator prompt may be up. It takes focus
    /// from the popup, which would otherwise hide itself out from under it.
    /// </summary>
    public event EventHandler<bool>? ModalStateChanged;

    /// <summary>
    /// Reads whether the daemon is running. Only one read runs at a time; a
    /// request that arrives during one makes it go around once more, so the
    /// result is never older than the request.
    /// </summary>
    public async Task RefreshAsync()
    {
        // Every caller is on the UI thread, so plain flags are enough.
        if (_busy)
            return;

        if (_refreshing)
        {
            _refreshAgain = true;
            return;
        }

        _refreshing = true;

        try
        {
            do
            {
                _refreshAgain = false;
                await ReadOnceAsync();
            }
            while (_refreshAgain);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task ReadOnceAsync()
    {
        RazerDaemonState? state = null;

        try
        {
            state = await Task.Run(() => _daemon.GetState()).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AppLog.Error("Could not read the state of razer-daemon.", exception);
        }

        if (state is { } known)
            await PostToUiAsync(() => ShowState(known));
    }

    private async Task ChangeAsync(bool start)
    {
        // Pressing the lit button changes nothing. Before the first read,
        // or with no daemon found, there is nothing to press.
        if (_busy || _state == RazerDaemonState.NotInstalled)
            return;

        if (start == (_state == RazerDaemonState.Running))
            return;

        _busy = true;
        _onButton.Enabled = false;
        _offButton.Enabled = false;
        ModalStateChanged?.Invoke(this, true);

        try
        {
            // Back on the UI thread afterwards: the click came from it.
            var result = await Task.Run(() => start ? _daemon.Start() : _daemon.Stop());

            // A failure is worth words in the header; a success shows in the buttons.
            if (!result.Success && !result.Cancelled)
                StatusChanged?.Invoke(this, new SectionStatus(result.Message, IsError: true));
        }
        catch (Exception exception)
        {
            AppLog.Error($"Could not {(start ? "start" : "stop")} razer-daemon.", exception);

            StatusChanged?.Invoke(
                this,
                new SectionStatus($"Could not {(start ? "start" : "stop")} razer-daemon.", IsError: true));
        }
        finally
        {
            _busy = false;
            _onButton.Enabled = true;
            _offButton.Enabled = true;
            ModalStateChanged?.Invoke(this, false);
        }

        // Whatever happened, show what is true now.
        await RefreshAsync();
    }

    private void ShowState(RazerDaemonState state)
    {
        var wasShown = _state != RazerDaemonState.NotInstalled;
        var isShown = state != RazerDaemonState.NotInstalled;

        _state = state;
        Visible = isShown;

        // Running lights On in green. Stopped is not a selection, just a state, so
        // Off gets a plain light outline instead of the green highlight.
        HighlightSelected([_onButton, _offButton], state == RazerDaemonState.Running ? _onButton : null);

        if (state == RazerDaemonState.Stopped)
            _offButton.FlatAppearance.BorderColor = StoppedOutlineColor;

        _toolTip.SetToolTip(_onButton, state == RazerDaemonState.Running ? "razer-daemon is running" : "Start razer-daemon");
        _toolTip.SetToolTip(_offButton, state == RazerDaemonState.Stopped ? "razer-daemon is stopped" : "Stop razer-daemon");

        if (wasShown != isShown)
            InstalledChanged?.Invoke(this, isShown);
    }

    private static Button CreateDaemonButton(string text, string name)
    {
        var button = CreateActionButton(text);
        button.Name = name;
        button.Font = GetDesignFont("Segoe UI", 9F, FontStyle.Bold);
        button.Margin = S(new Padding(2, 2, 2, 8));
        return button;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _toolTip.Dispose();

        base.Dispose(disposing);
    }
}
