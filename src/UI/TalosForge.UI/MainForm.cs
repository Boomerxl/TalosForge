using System.Text;
using TalosForge.Core.Configuration;
using TalosForge.Core.Models;
using TalosForge.Core.Runtime;

namespace TalosForge.UI;

public sealed class MainForm : Form
{
    private readonly Button _startButton;
    private readonly Button _stopButton;
    private readonly TextBox _pluginDirText;
    private readonly ComboBox _telemetryLevelCombo;
    private readonly NumericUpDown _telemetryIntervalInput;
    private readonly CheckBox _inGameOverlayCheck;
    private readonly NumericUpDown _inGameOverlayIntervalInput;
    private readonly Label _statusValue;
    private readonly Label _tickValue;
    private readonly Label _objectsValue;
    private readonly Label _targetValue;
    private readonly Label _commandsValue;
    private readonly RichTextBox _logBox;

    private CancellationTokenSource? _runCts;
    private Task? _runTask;

    public MainForm()
    {
        Text = "TalosForge Control";
        Width = 1100;
        Height = 760;
        StartPosition = FormStartPosition.CenterScreen;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(10),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var topControls = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 12,
        };
        topControls.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        topControls.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        topControls.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        topControls.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        topControls.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        topControls.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        topControls.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        topControls.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        topControls.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        topControls.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        topControls.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        topControls.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _startButton = new Button { Text = "Start", Dock = DockStyle.Fill };
        _stopButton = new Button { Text = "Stop", Dock = DockStyle.Fill, Enabled = false };

        _startButton.Click += StartButton_Click;
        _stopButton.Click += StopButton_Click;

        _telemetryLevelCombo = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _telemetryLevelCombo.Items.AddRange(new object[] { "minimal", "normal", "debug" });
        _telemetryLevelCombo.SelectedItem = "normal";

        _telemetryIntervalInput = new NumericUpDown
        {
            Dock = DockStyle.Fill,
            Minimum = 0,
            Maximum = 500,
            Value = 10,
        };

        _pluginDirText = new TextBox
        {
            Dock = DockStyle.Fill,
            PlaceholderText = "Optional --plugin-dir override",
        };

        _inGameOverlayCheck = new CheckBox
        {
            Dock = DockStyle.Fill,
            Checked = false,
            Text = "Enable",
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _inGameOverlayIntervalInput = new NumericUpDown
        {
            Dock = DockStyle.Fill,
            Minimum = 1,
            Maximum = 500,
            Value = 10,
        };

        topControls.Controls.Add(_startButton, 0, 0);
        topControls.Controls.Add(_stopButton, 1, 0);
        topControls.Controls.Add(new Label { Text = "Telemetry Level", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 2, 0);
        topControls.Controls.Add(_telemetryLevelCombo, 3, 0);
        topControls.Controls.Add(new Label { Text = "Telemetry Interval", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 4, 0);
        topControls.Controls.Add(_telemetryIntervalInput, 5, 0);
        topControls.Controls.Add(new Label { Text = "In-game UI", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 6, 0);
        topControls.Controls.Add(_inGameOverlayCheck, 7, 0);
        topControls.Controls.Add(new Label { Text = "Overlay Interval", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 8, 0);
        topControls.Controls.Add(_inGameOverlayIntervalInput, 9, 0);
        topControls.Controls.Add(new Label { Text = "Plugin Dir", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 10, 0);
        topControls.Controls.Add(_pluginDirText, 11, 0);

        var metrics = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 10,
        };
        for (var i = 0; i < 10; i++)
        {
            metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 10));
        }

        _statusValue = MakeValueLabel("Stopped");
        _tickValue = MakeValueLabel("-");
        _objectsValue = MakeValueLabel("-");
        _targetValue = MakeValueLabel("-");
        _commandsValue = MakeValueLabel("-");

        metrics.Controls.Add(MakeHeaderLabel("Status"), 0, 0);
        metrics.Controls.Add(_statusValue, 1, 0);
        metrics.Controls.Add(MakeHeaderLabel("Tick"), 2, 0);
        metrics.Controls.Add(_tickValue, 3, 0);
        metrics.Controls.Add(MakeHeaderLabel("Objects"), 4, 0);
        metrics.Controls.Add(_objectsValue, 5, 0);
        metrics.Controls.Add(MakeHeaderLabel("Target"), 6, 0);
        metrics.Controls.Add(_targetValue, 7, 0);
        metrics.Controls.Add(MakeHeaderLabel("Commands"), 8, 0);
        metrics.Controls.Add(_commandsValue, 9, 0);

        var logTitle = new Label
        {
            Text = "Runtime Log",
            Dock = DockStyle.Fill,
            Font = new Font(Font, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _logBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            Font = new Font("Consolas", 10f),
        };

        root.Controls.Add(topControls, 0, 0);
        root.Controls.Add(metrics, 0, 1);
        root.Controls.Add(logTitle, 0, 2);
        root.Controls.Add(_logBox, 0, 3);

        Controls.Add(root);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_runTask != null && !_runTask.IsCompleted)
        {
            _runCts?.Cancel();
        }

        base.OnFormClosing(e);
    }

    private void StartButton_Click(object? sender, EventArgs e)
    {
        if (_runTask != null && !_runTask.IsCompleted)
        {
            return;
        }

        var level = ParseTelemetryLevel(_telemetryLevelCombo.SelectedItem?.ToString());
        var interval = Decimal.ToInt32(_telemetryIntervalInput.Value);

        var options = new BotOptions
        {
            TelemetryLevel = level,
            SnapshotTelemetryEveryTicks = interval,
            EnableSnapshotTelemetry = interval > 0,
            EnableInGameOverlay = _inGameOverlayCheck.Checked,
            InGameOverlayEveryTicks = Decimal.ToInt32(_inGameOverlayIntervalInput.Value),
        };

        var runtime = new RuntimeOptions
        {
            PluginDirectoryOverride = string.IsNullOrWhiteSpace(_pluginDirText.Text) ? null : _pluginDirText.Text.Trim(),
            SmokeMode = false,
        };

        _runCts = new CancellationTokenSource();
        var host = new BotRuntimeHost(options, runtime, AppendLogSafe, OnTickSafe);

        SetRunningState(true);
        AppendLogSafe("Starting runtime host...");

        _runTask = Task.Run(async () => await host.RunAsync(_runCts.Token).ConfigureAwait(false));
        _ = _runTask.ContinueWith(_ =>
        {
            if (IsHandleCreated)
            {
                BeginInvoke(new Action(() =>
                {
                    SetRunningState(false);
                    AppendLogSafe("Runtime stopped.");
                }));
            }
        });
    }

    private void StopButton_Click(object? sender, EventArgs e)
    {
        if (_runTask == null || _runTask.IsCompleted)
        {
            return;
        }

        _runCts?.Cancel();
        AppendLogSafe("Stopping runtime host...");
    }

    private void OnTickSafe(BotTickMetrics metrics, WorldSnapshot snapshot)
    {
        if (!IsHandleCreated)
        {
            return;
        }

        BeginInvoke(new Action(() =>
        {
            _statusValue.Text = snapshot.Success ? "Running" : "Error";
            _tickValue.Text = metrics.TickId.ToString();
            _objectsValue.Text = snapshot.Objects.Count.ToString();
            _targetValue.Text = FormatGuid(snapshot.Player?.TargetGuid);
            _commandsValue.Text = metrics.CommandsCount.ToString();
        }));
    }

    private void AppendLogSafe(string line)
    {
        if (!IsHandleCreated)
        {
            return;
        }

        BeginInvoke(new Action(() =>
        {
            var sb = new StringBuilder();
            sb.AppendLine(line);
            _logBox.AppendText(sb.ToString());
            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.ScrollToCaret();
        }));
    }

    private void SetRunningState(bool running)
    {
        _startButton.Enabled = !running;
        _stopButton.Enabled = running;
        _statusValue.Text = running ? "Running" : "Stopped";
    }

    private static TelemetryLevel ParseTelemetryLevel(string? value)
    {
        return value?.ToLowerInvariant() switch
        {
            "minimal" => TelemetryLevel.Minimal,
            "debug" => TelemetryLevel.Debug,
            _ => TelemetryLevel.Normal,
        };
    }

    private static string FormatGuid(ulong? guid)
    {
        return guid.HasValue && guid.Value != 0 ? $"0x{guid.Value:X16}" : "none";
    }

    private static Label MakeHeaderLabel(string text)
    {
        return new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
        };
    }

    private static Label MakeValueLabel(string text)
    {
        return new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        };
    }
}
