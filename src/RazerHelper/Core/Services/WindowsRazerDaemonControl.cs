using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Xml.Linq;
using RazerHelper.Core.Diagnostics;

namespace RazerHelper.Core.Services;

/// <summary>
/// The real thing. razer-daemon runs elevated, started at logon by a Task
/// Scheduler task called "RazerDaemon" (see razer-control-win's
/// install-daemon-task.ps1). This app does not run elevated, so:
/// <list type="bullet">
/// <item><b>Stop</b> asks the daemon to exit over its own local port, which
/// needs no rights. Only if it does not answer is Windows asked to end it,
/// which shows one administrator prompt.</item>
/// <item><b>Start</b> runs that same scheduled task, which needs no prompt
/// either. Only if there is no task is the program started directly, which
/// shows the administrator prompt, because the daemon asks for it.</item>
/// </list>
/// </summary>
internal sealed class WindowsRazerDaemonControl : IRazerDaemonControl
{
    private const string ProcessName = "razer-daemon";
    private const string ExecutableName = "razer-daemon.exe";
    private const string TaskName = "RazerDaemon";

    // Windows' error for "the user cancelled the elevation prompt".
    private const int ErrorCancelled = 1223;

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(150);

    // A PC without the daemon would otherwise run schtasks.exe every time the popup opens.
    private static readonly TimeSpan NotInstalledRecheck = TimeSpan.FromMinutes(5);

    private DateTime _notInstalledUntil = DateTime.MinValue;

    public RazerDaemonState GetState()
    {
        if (IsRunning())
        {
            _notInstalledUntil = DateTime.MinValue;
            return RazerDaemonState.Running;
        }

        if (DateTime.UtcNow < _notInstalledUntil)
            return RazerDaemonState.NotInstalled;

        if (IsTaskRegistered() || FindExecutable() is not null)
            return RazerDaemonState.Stopped;

        _notInstalledUntil = DateTime.UtcNow + NotInstalledRecheck;
        return RazerDaemonState.NotInstalled;
    }

    public DaemonActionResult Start()
    {
        if (IsRunning())
            return DaemonActionResult.Ok;

        // The scheduled task starts it elevated, with no prompt.
        if (RunHidden("schtasks.exe", $"/Run /TN {TaskName}").ExitCode == 0)
        {
            AppLog.Info("razer-daemon: started through its scheduled task.");

            return WaitUntil(IsRunning, StartTimeout)
                ? DaemonActionResult.Ok
                : DaemonActionResult.Failed("razer-daemon started, then exited. Is another copy holding its port?");
        }

        var executable = FindExecutable();

        if (executable is null)
            return DaemonActionResult.Failed("razer-daemon.exe was not found.");

        try
        {
            // The daemon's manifest asks for administrator rights, so Windows prompts.
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? string.Empty,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorCancelled)
        {
            return DaemonActionResult.Declined;
        }
        catch (Win32Exception exception)
        {
            AppLog.Error("razer-daemon: could not be started.", exception);
            return DaemonActionResult.Failed("Could not start razer-daemon.");
        }

        AppLog.Info($"razer-daemon: started from {executable}.");

        return WaitUntil(IsRunning, StartTimeout)
            ? DaemonActionResult.Ok
            : DaemonActionResult.Failed("razer-daemon started, then exited. Is another copy holding its port?");
    }

    public DaemonActionResult Stop()
    {
        if (!IsRunning())
            return DaemonActionResult.Ok;

        // The polite way: the daemon saves its effects, then exits.
        if (SendShutdown() && WaitUntil(() => !IsRunning(), StopTimeout))
        {
            AppLog.Info("razer-daemon: stopped through its own shutdown command.");
            return DaemonActionResult.Ok;
        }

        // It is running but did not answer (hung, or an old build without the
        // command). Ask Windows to end it; that needs administrator rights.
        AppLog.Info("razer-daemon: did not answer the shutdown command; asking Windows to end it.");

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "taskkill.exe",
                Arguments = $"/F /IM {ExecutableName}",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });

            process?.WaitForExit((int)StopTimeout.TotalMilliseconds);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorCancelled)
        {
            return DaemonActionResult.Declined;
        }
        catch (Win32Exception exception)
        {
            AppLog.Error("razer-daemon: could not be ended.", exception);
            return DaemonActionResult.Failed("Could not stop razer-daemon.");
        }

        return WaitUntil(() => !IsRunning(), StopTimeout)
            ? DaemonActionResult.Ok
            : DaemonActionResult.Failed("razer-daemon is still running.");
    }

    private static bool IsRunning()
    {
        var processes = Process.GetProcessesByName(ProcessName);

        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    // Sends the one command and does not wait for a reply: the daemon exits
    // as it handles it.
    private static bool SendShutdown()
    {
        try
        {
            using var client = new TcpClient();

            if (!client.ConnectAsync(RazerDaemonIpc.Address, RazerDaemonIpc.Port).Wait(1000))
                return false;

            var stream = client.GetStream();
            stream.WriteTimeout = 1000;
            stream.Write(RazerDaemonIpc.ShutdownCommand());
            stream.Flush();
            return true;
        }
        catch (Exception exception) when (exception is SocketException or IOException or AggregateException or ObjectDisposedException)
        {
            return false;
        }
    }

    private static bool IsTaskRegistered() =>
        RunHidden("schtasks.exe", $"/Query /TN {TaskName}").ExitCode == 0;

    // Where razer-daemon.exe is: the scheduled task knows, then next to this
    // app, then where the installer of razer-control-win puts it.
    private static string? FindExecutable()
    {
        var candidates = new List<string>();

        if (ReadTaskCommand() is { } fromTask)
            candidates.Add(fromTask);

        candidates.Add(Path.Combine(AppContext.BaseDirectory, ExecutableName));
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Razer Blade Control", ExecutableName));
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Razer Blade Control", ExecutableName));

        return candidates.FirstOrDefault(File.Exists);
    }

    // The program the scheduled task runs, read from the task's own definition.
    private static string? ReadTaskCommand()
    {
        var (exitCode, output) = RunHidden("schtasks.exe", $"/Query /TN {TaskName} /XML");

        if (exitCode != 0 || string.IsNullOrWhiteSpace(output))
            return null;

        try
        {
            var command = XDocument.Parse(output.Trim())
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "Command")?
                .Value
                .Trim()
                .Trim('"');

            return string.IsNullOrEmpty(command) ? null : Environment.ExpandEnvironmentVariables(command);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            if (condition())
                return true;

            if (DateTime.UtcNow >= deadline)
                return false;

            Thread.Sleep(PollInterval);
        }
    }

    // Runs a console program with no window and returns how it ended.
    private static (int ExitCode, string Output) RunHidden(string fileName, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            });

            if (process is null)
                return (-1, string.Empty);

            // Read both streams while it runs so a full pipe cannot stall it.
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(5000))
            {
                process.Kill();
                return (-1, string.Empty);
            }

            process.WaitForExit();
            error.GetAwaiter().GetResult();
            return (process.ExitCode, output.GetAwaiter().GetResult());
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            AppLog.Error($"Could not run {fileName}.", exception);
            return (-1, string.Empty);
        }
    }
}
