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
    private readonly CheckBox _useMockUnlockerCheck;
    private readonly Label _unlockerBadge;

    private readonly Label _statusValue;
    private readonly Label _tickValue;
    private readonly Label _objectsValue;
    private readonly Label _targetValue;
    private readonly Label _commandsValue;
    private readonly Label _unlockerValue;
    private readonly RichTextBox _logBox;
    private readonly ToolTip _toolTip;

    private CancellationTokenSource? _runCts;
    private Task? _runTask;

    public MainForm()
    {
        Text = "TalosForge Control";
        Width = 1120;
        Height = 760;
        MinimumSize = new Size(900, 620);
        StartPosition = FormStartPosition.CenterScreen;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(10),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 122));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var topControls = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(2),
        };

        _startButton = new Button { Text = "Start", Size = new Size(90, 34) };
        _stopButton = new Button { Text = "Stop", Size = new Size(90, 34), Enabled = false };
        _startButton.Click += StartButton_Click;
        _stopButton.Click += StopButton_Click;
        _unlockerBadge = new Label
        {
            Text = "Unlocker: Unknown",
            AutoSize = false,
            Width = 200,
            Height = 34,
            TextAlign = ContentAlignment.MiddleCenter,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.Gainsboro,
            Margin = new Padding(8, 0, 8, 0),
        };

        _telemetryLevelCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 120,
        };
        _telemetryLevelCombo.Items.AddRange(new object[] { "minimal", "normal", "debug" });
        _telemetryLevelCombo.SelectedItem = "normal";

        _telemetryIntervalInput = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 500,
            Value = 10,
            Width = 90,
        };

        _inGameOverlayCheck = new CheckBox
        {
            Text = "Enabled",
            AutoSize = true,
            Checked = false,
        };

        _inGameOverlayIntervalInput = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 500,
            Value = 10,
            Width = 90,
        };

        _useMockUnlockerCheck = new CheckBox
        {
            Text = "Use Mock Unlocker",
            AutoSize = true,
            Checked = false,
        };

        _pluginDirText = new TextBox
        {
            Width = 320,
            PlaceholderText = "Optional plugin directory override",
        };

        topControls.Controls.Add(_startButton);
        topControls.Controls.Add(_stopButton);
        topControls.Controls.Add(_unlockerBadge);
        topControls.Controls.Add(MakeInlineGroup("Telemetry Level", _telemetryLevelCombo));
        topControls.Controls.Add(MakeInlineGroup("Telemetry Interval", _telemetryIntervalInput));
        topControls.Controls.Add(MakeInlineGroup("In-game UI", _inGameOverlayCheck));
        topControls.Controls.Add(MakeInlineGroup("Overlay Interval", _inGameOverlayIntervalInput));
        topControls.Controls.Add(_useMockUnlockerCheck);
        topControls.Controls.Add(MakeInlineGroup("Plugin Dir", _pluginDirText));

        var metrics = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 12,
            Padding = new Padding(4, 0, 4, 0),
        };
        for (var i = 0; i < 12; i++)
        {
            metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 12f));
        }

        _statusValue = MakeValueLabel("Stopped");
        _tickValue = MakeValueLabel("-");
        _objectsValue = MakeValueLabel("-");
        _targetValue = MakeValueLabel("-");
        _commandsValue = MakeValueLabel("-");
        _unlockerValue = MakeValueLabel("Unknown");

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
        metrics.Controls.Add(MakeHeaderLabel("Unlocker"), 10, 0);
        metrics.Controls.Add(_unlockerValue, 11, 0);

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
        _toolTip = new ToolTip();

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
            UseMockUnlocker = _useMockUnlockerCheck.Checked,
        };

        _runCts = new CancellationTokenSource();
        var host = new BotRuntimeHost(options, runtime, AppendLogSafe, OnTickSafe, OnUnlockerHealthSafe);

        SetRunningState(true);
        AppendLogSafe("Starting runtime host...");
        AppendLogSafe(runtime.UseMockUnlocker
            ? "Mode: Mock unlocker (in-game effects simulated)."
            : "Mode: Real unlocker (external endpoint required for in-game effects).");

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

    private void OnUnlockerHealthSafe(UnlockerHealthSnapshot health)
    {
        if (!IsHandleCreated)
        {
            return;
        }

        BeginInvoke(new Action(() =>
        {
            UpdateUnlockerUi(health.State, health.Summary);
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
        if (!running)
        {
            UpdateUnlockerUi(UnlockerConnectionState.Unknown, "Not running");
        }
    }

    private void UpdateUnlockerUi(UnlockerConnectionState state, string summary)
    {
        var stateText = state switch
        {
            UnlockerConnectionState.Connected => "Connected",
            UnlockerConnectionState.Degraded => "Degraded",
            UnlockerConnectionState.Disconnected => "Disconnected",
            _ => "Unknown",
        };

        var color = state switch
        {
            UnlockerConnectionState.Connected => Color.ForestGreen,
            UnlockerConnectionState.Degraded => Color.DarkOrange,
            UnlockerConnectionState.Disconnected => Color.Firebrick,
            _ => SystemColors.ControlText,
        };

        _unlockerValue.Text = stateText;
        _unlockerValue.ForeColor = color;

        _unlockerBadge.Text = $"Unlocker: {stateText}";
        _unlockerBadge.BackColor = state switch
        {
            UnlockerConnectionState.Connected => Color.Honeydew,
            UnlockerConnectionState.Degraded => Color.LemonChiffon,
            UnlockerConnectionState.Disconnected => Color.MistyRose,
            _ => Color.Gainsboro,
        };
        _unlockerBadge.ForeColor = color;
        _unlockerBadge.Refresh();

        if (!string.IsNullOrWhiteSpace(summary))
        {
            _toolTip.SetToolTip(_unlockerBadge, summary);
        }
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

    private static Panel MakeInlineGroup(string label, Control control)
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(8, 6, 8, 6),
        };

        panel.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 8, 6, 0),
        });
        panel.Controls.Add(control);
        return panel;
    }
}
