using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace TalosForge.UI;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<SupervisorProcessView> _processes = new();
    private readonly DispatcherTimer _refreshTimer;
    private bool _busy;
    private string? _supervisorScriptPath;

    public ObservableCollection<SupervisorProcessView> Processes => _processes;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += async (_, _) => await RefreshStatusAsync();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyDarkTitleBar();
            _supervisorScriptPath = ResolveSupervisorScriptPath();
            await RefreshStatusAsync();
            _refreshTimer.Start();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Status: supervisor unavailable";
            MessageBox.Show(
                $"Failed to initialize supervisor UI:\n{ex.Message}",
                "TalosForge Supervisor",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _refreshTimer.Stop();
    }

    private async void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        await RunActionAsync("start");
    }

    private async void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        await RunActionAsync("stop");
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshStatusAsync();
    }

    private async Task RunActionAsync(string action)
    {
        if (_busy || _supervisorScriptPath is null)
        {
            return;
        }

        _busy = true;
        UpdateButtons();
        try
        {
            var status = await InvokeSupervisorAsync(action);
            ApplyStatus(status);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Supervisor {action} failed:\n{ex.Message}",
                "TalosForge Supervisor",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }
    }

    private async Task RefreshStatusAsync()
    {
        if (_busy || _supervisorScriptPath is null)
        {
            return;
        }

        _busy = true;
        UpdateButtons();
        try
        {
            var status = await InvokeSupervisorAsync("status");
            ApplyStatus(status);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Status: refresh failed";
            MessageBox.Show(
                $"Failed to query supervisor status:\n{ex.Message}",
                "TalosForge Supervisor",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }
    }

    private void ApplyStatus(SupervisorStatus status)
    {
        _processes.Clear();
        if (status.processes != null)
        {
            foreach (var p in status.processes)
            {
                _processes.Add(new SupervisorProcessView
                {
                    Name = p.name ?? string.Empty,
                    Pid = p.pid,
                    Status = p.status ?? string.Empty,
                    OutLog = p.outLog ?? string.Empty,
                    ErrLog = p.errLog ?? string.Empty
                });
            }
        }

        StatusText.Text =
            $"Status: {status.status ?? "unknown"} ({status.runningProcesses}/{status.expectedProcesses} running)";
        SessionText.Text = $"Session ID: {status.sessionId ?? "n/a"}";
        RunDirText.Text = $"Run Dir: {status.runDir ?? "n/a"}";

        HealthJsonTextBox.Text = JsonSerializer.Serialize(
            status,
            new JsonSerializerOptions
            {
                WriteIndented = true
            });
    }

    private void UpdateButtons()
    {
        BtnStart.IsEnabled = !_busy;
        BtnStop.IsEnabled = !_busy;
        BtnRefresh.IsEnabled = !_busy;
    }

    private async Task<SupervisorStatus> InvokeSupervisorAsync(string action)
    {
        if (string.Equals(action, "status", StringComparison.OrdinalIgnoreCase))
        {
            return await QuerySupervisorStatusAsync();
        }

        await InvokeSupervisorActionAsync(action);
        return await QuerySupervisorStatusAsync();
    }

    private async Task InvokeSupervisorActionAsync(string action)
    {
        if (_supervisorScriptPath is null)
        {
            throw new InvalidOperationException("Supervisor script path is not initialized.");
        }

        var scriptDir = Path.GetDirectoryName(_supervisorScriptPath)!;
        var repoRoot = Directory.GetParent(scriptDir)!.FullName;
        var args = string.Join(
            " ",
            [
                "-NoProfile",
                "-ExecutionPolicy", "Bypass",
                "-File", Quote(_supervisorScriptPath),
                "-Action", action,
                "-Configuration", "Release",
                "-BridgeMode", "wow-agent"
            ]);

        var psi = new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = args,
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start powershell for supervisor action.");

        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var stderr = (await stderrTask).Trim();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(stderr)
                    ? $"Supervisor action '{action}' exited with code {process.ExitCode}."
                    : stderr);
        }
    }

    private async Task<SupervisorStatus> QuerySupervisorStatusAsync()
    {
        if (_supervisorScriptPath is null)
        {
            throw new InvalidOperationException("Supervisor script path is not initialized.");
        }

        var scriptDir = Path.GetDirectoryName(_supervisorScriptPath)!;
        var repoRoot = Directory.GetParent(scriptDir)!.FullName;
        var args = string.Join(
            " ",
            [
                "-NoProfile",
                "-ExecutionPolicy", "Bypass",
                "-File", Quote(_supervisorScriptPath),
                "-Action", "status",
                "-Configuration", "Release",
                "-BridgeMode", "wow-agent",
                "-Json"
            ]);

        var psi = new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = args,
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start powershell for supervisor command.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var stdout = (await stdoutTask).Trim();
        var stderr = (await stderrTask).Trim();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(stderr)
                    ? $"Supervisor status exited with code {process.ExitCode}. Output: {stdout}"
                    : stderr);
        }

        if (string.IsNullOrWhiteSpace(stdout))
        {
            throw new InvalidOperationException("Supervisor returned no JSON output.");
        }

        var status = JsonSerializer.Deserialize<SupervisorStatus>(
            stdout,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

        return status ?? throw new InvalidOperationException("Supervisor JSON payload was empty.");
    }

    private static string ResolveSupervisorScriptPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 16 && dir != null; i++)
        {
            var candidate = Path.Combine(dir.FullName, "scripts", "supervisor.ps1");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("Unable to locate scripts/supervisor.ps1 from UI runtime directory.");
    }

    private static string Quote(string value) => $"\"{value}\"";

    private void ApplyDarkTitleBar()
    {
        try
        {
            if (PresentationSource.FromVisual(this) is HwndSource source)
            {
                var value = 1;
                DwmSetWindowAttribute(source.Handle, 20, ref value, sizeof(int));
            }
        }
        catch
        {
            // optional visual polish only
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
}

public sealed class SupervisorProcessView
{
    public string Name { get; set; } = string.Empty;
    public int Pid { get; set; }
    public string Status { get; set; } = string.Empty;
    public string OutLog { get; set; } = string.Empty;
    public string ErrLog { get; set; } = string.Empty;
}

public sealed class SupervisorStatus
{
    public string? status { get; set; }
    public bool healthy { get; set; }
    public string? sessionId { get; set; }
    public string? startedUtc { get; set; }
    public string? configuration { get; set; }
    public string? bridgeMode { get; set; }
    public string? pipeName { get; set; }
    public string? agentPipeName { get; set; }
    public string? runDir { get; set; }
    public int expectedProcesses { get; set; }
    public int runningProcesses { get; set; }
    public List<SupervisorProcessStatus>? processes { get; set; }
}

public sealed class SupervisorProcessStatus
{
    public string? name { get; set; }
    public int pid { get; set; }
    public string? status { get; set; }
    public string? outLog { get; set; }
    public string? errLog { get; set; }
}
