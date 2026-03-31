using System.Text.Json;
using TalosForge.Core.Abstractions;
using TalosForge.Core.Configuration;
using TalosForge.Core.Models;

namespace TalosForge.Core.Drawing;

/// <summary>
/// Addon-style in-game hub: pushes state via LuaDoString and drains user actions via LuaQuery.
/// </summary>
public sealed class TalosForgeHubOverlayService
{
    private const int MinPublishIntervalMs = 1000;

    private readonly IUnlockerClient _unlockerClient;
    private readonly BotOptions _options;
    private readonly Func<IReadOnlyList<string>> _pluginNames;
    private long _lastPublishUnixMs;
    private bool _overlayVisible;
    private bool _hubLuaCreated;

    public TalosForgeHubOverlayService(
        IUnlockerClient unlockerClient,
        BotOptions options,
        Func<IReadOnlyList<string>>? pluginNamesProvider = null)
    {
        _unlockerClient = unlockerClient;
        _options = options;
        _pluginNames = pluginNamesProvider ?? (() => Array.Empty<string>());
    }

    /// <summary>Total unlocker commands issued this tick (LuaDoString / LuaQuery).</summary>
    public async Task<int> TryPublishAsync(
        long tickId,
        BotState state,
        WorldSnapshot snapshot,
        int queuedCommands,
        BotTickMetrics? lastTickMetrics,
        CancellationToken cancellationToken)
    {
        if (!_options.EnableInGameOverlay)
        {
            return 0;
        }

        if (!ShouldRender(snapshot))
        {
            if (!_overlayVisible)
            {
                return 0;
            }

            _overlayVisible = false;
            await SendLuaDoStringAsync(TalosForgeFrameLua.BuildHideLua(), tickId, cancellationToken)
                .ConfigureAwait(false);
            return 1;
        }

        var interval = _options.InGameOverlayEveryTicks;
        var forcePublish = !_overlayVisible;
        if (!forcePublish && (interval <= 0 || tickId % interval != 0))
        {
            _overlayVisible = true;
            return 0;
        }

        var nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!forcePublish && nowUnixMs - _lastPublishUnixMs < MinPublishIntervalMs)
        {
            _overlayVisible = true;
            return 0;
        }

        _lastPublishUnixMs = nowUnixMs;
        _overlayVisible = true;

        var names = _pluginNames();
        var debugBody = HubOverlayViewBuilder.BuildDebugSection(
            snapshot,
            lastTickMetrics,
            state,
            tickId,
            queuedCommands);
        var pluginsBody = HubOverlayViewBuilder.BuildPluginsSection(names);
        var update = TalosForgeFrameLua.BuildHubUpdate(debugBody, pluginsBody);
        var lua = _hubLuaCreated
            ? update
            : TalosForgeFrameLua.CreateHub() + "\n" + update;

        _hubLuaCreated = true;

        await SendLuaDoStringAsync(lua, nowUnixMs, cancellationToken).ConfigureAwait(false);
        return 1;
    }

    private async Task SendLuaDoStringAsync(string lua, long commandId, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { code = lua });
        var command = new UnlockerCommand(
            commandId,
            UnlockerOpcode.LuaDoString,
            payload,
            DateTimeOffset.UtcNow);
        await _unlockerClient.SendAsync(command, cancellationToken).ConfigureAwait(false);
    }

    internal static bool ShouldRender(WorldSnapshot snapshot)
    {
        return snapshot.Success && snapshot.Player is not null;
    }
}

/// <summary>Parses LuaQuery results from <see cref="TalosForgeFrameLua.PendingPollScript"/>.</summary>
public static class HubPendingActionParser
{
    public static bool TryParse(string? message, out string kind, out string payload)
    {
        kind = "";
        payload = "";
        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        if (message.StartsWith("ACK:", StringComparison.Ordinal))
        {
            return false;
        }

        var i = message.IndexOf('\u0001');
        if (i <= 0)
        {
            return false;
        }

        kind = message[..i];
        payload = message[(i + 1)..];
        return kind.Length > 0;
    }
}
