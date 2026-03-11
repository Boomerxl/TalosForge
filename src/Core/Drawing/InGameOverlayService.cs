using System.Text.Json;
using TalosForge.Core.Abstractions;
using TalosForge.Core.Configuration;
using TalosForge.Core.Models;

namespace TalosForge.Core.Drawing;

/// <summary>
/// Sends lightweight in-game status overlay text through unlocker Lua execution.
/// </summary>
public sealed class InGameOverlayService
{
    private readonly IUnlockerClient _unlockerClient;
    private readonly BotOptions _options;

    public InGameOverlayService(IUnlockerClient unlockerClient, BotOptions options)
    {
        _unlockerClient = unlockerClient;
        _options = options;
    }

    public async Task<int> TryPublishAsync(
        long tickId,
        BotState state,
        WorldSnapshot snapshot,
        int queuedCommands,
        CancellationToken cancellationToken)
    {
        if (!_options.EnableInGameOverlay)
        {
            return 0;
        }

        var interval = _options.InGameOverlayEveryTicks;
        if (interval <= 0 || tickId % interval != 0)
        {
            return 0;
        }

        var message = BuildOverlayMessage(tickId, state, snapshot, queuedCommands);
        var lua = BuildLua(message);
        var payload = JsonSerializer.Serialize(new { code = lua });

        var command = new UnlockerCommand(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            UnlockerOpcode.LuaDoString,
            payload,
            DateTimeOffset.UtcNow);

        await _unlockerClient.SendAsync(command, cancellationToken).ConfigureAwait(false);
        return 1;
    }

    internal static string BuildLua(string message)
    {
        var safe = (message ?? string.Empty).Replace("]]", "] ]");

        return "local frame = _G['TalosForgeStatusFrame'];" +
               "if not frame then " +
               "frame = CreateFrame('Frame','TalosForgeStatusFrame',UIParent);" +
               "end;" +
               "frame:ClearAllPoints();" +
               "frame:SetSize(900,80);" +
               "frame:SetPoint('CENTER', UIParent, 'CENTER', 0, 0);" +
               "frame:SetFrameStrata('TOOLTIP');" +
               "frame:SetFrameLevel(999);" +
               "if not frame.TalosForgeBg then " +
               "frame.TalosForgeBg = frame:CreateTexture(nil,'BACKGROUND');" +
               "end;" +
               "frame.TalosForgeBg:SetAllPoints(true);" +
               "frame.TalosForgeBg:SetColorTexture(0,0,0,0.55);" +
               "local text = _G['TalosForgeStatusText'];" +
               "if not text then " +
               "text = frame:CreateFontString('TalosForgeStatusText','OVERLAY');" +
               "end;" +
               "text:ClearAllPoints();" +
               "text:SetPoint('CENTER', frame, 'CENTER', 0, 0);" +
               "text:SetFont('Fonts\\\\FRIZQT__.TTF',24,'OUTLINE');" +
               "text:SetTextColor(0.1,1.0,0.1,1.0);" +
               "text:SetShadowOffset(2,-2);" +
               "text:SetShadowColor(0,0,0,1);" +
               "TalosForgeStatusFrame:Show();" +
               "TalosForgeStatusText:SetText([[" + safe + "]]);";
    }

    private static string BuildOverlayMessage(long tickId, BotState state, WorldSnapshot snapshot, int queuedCommands)
    {
        var target = snapshot.Player?.TargetGuid is { } targetGuid && targetGuid != 0
            ? $"0x{targetGuid:X16}"
            : "none";

        var status = snapshot.Success ? "ok" : "err";
        return $"TalosForge [{status}] Tick:{tickId} State:{state} Obj:{snapshot.Objects.Count} Target:{target} Cmd:{queuedCommands}";
    }
}
