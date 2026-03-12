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

        // Avoid issuing Lua into non-world/login states where game-side script
        // context can be unstable and pointers are not yet valid.
        if (!snapshot.Success || snapshot.Player is null)
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
               "frame:SetSize(460,132);" +
               "frame:SetPoint('TOPLEFT',UIParent,'TOPLEFT',24,-220);" +
               "frame:SetFrameStrata('TOOLTIP');" +
               "frame:SetFrameLevel(9999);" +
               "frame:SetMovable(true);" +
               "frame:EnableMouse(true);" +
               "frame:RegisterForDrag('LeftButton');" +
               "frame:SetScript('OnDragStart', function(self) self:StartMoving() end);" +
               "frame:SetScript('OnDragStop', function(self) self:StopMovingOrSizing() end);" +
               "local bg = frame:CreateTexture(nil,'BACKGROUND');" +
               "bg:SetAllPoints(true);" +
               "bg:SetTexture(0,0,0,0.72);" +
               "frame.TalosForgeBg = bg;" +
               "local titleBg = frame:CreateTexture(nil,'BORDER');" +
               "titleBg:SetPoint('TOPLEFT',frame,'TOPLEFT',0,0);" +
               "titleBg:SetPoint('TOPRIGHT',frame,'TOPRIGHT',0,0);" +
               "titleBg:SetHeight(22);" +
               "titleBg:SetTexture(0.08,0.28,0.08,0.95);" +
               "frame.TalosForgeTitleBg = titleBg;" +
               "local title = frame:CreateFontString(nil,'OVERLAY');" +
               "if GameFontNormalLarge then title:SetFontObject(GameFontNormalLarge) elseif GameFontNormal then title:SetFontObject(GameFontNormal) elseif ChatFontNormal then title:SetFontObject(ChatFontNormal) end;" +
               "title:SetPoint('TOPLEFT',frame,'TOPLEFT',8,-4);" +
               "title:SetTextColor(0.2,1.0,0.2,1.0);" +
               "title:SetShadowOffset(1,-1);" +
               "title:SetShadowColor(0,0,0,1);" +
               "title:SetText('TalosForge Native');" +
               "frame.TalosForgeTitle = title;" +
               "local text = frame:CreateFontString(nil,'OVERLAY');" +
               "if GameFontHighlightSmall then text:SetFontObject(GameFontHighlightSmall) elseif GameFontNormalSmall then text:SetFontObject(GameFontNormalSmall) elseif ChatFontNormal then text:SetFontObject(ChatFontNormal) end;" +
               "text:SetPoint('TOPLEFT',frame,'TOPLEFT',10,-30);" +
               "text:SetPoint('BOTTOMRIGHT',frame,'BOTTOMRIGHT',-10,10);" +
               "text:SetJustifyH('LEFT');" +
               "text:SetJustifyV('TOP');" +
               "text:SetTextColor(0.82,0.95,0.82,1);" +
               "text:SetShadowOffset(1,-1);" +
               "text:SetShadowColor(0,0,0,1);" +
               "frame.TalosForgeText = text;" +
               "end;" +
               "if not frame.TalosForgeText then " +
               "local text = frame:CreateFontString(nil,'OVERLAY');" +
               "if GameFontHighlightSmall then text:SetFontObject(GameFontHighlightSmall) elseif GameFontNormalSmall then text:SetFontObject(GameFontNormalSmall) elseif ChatFontNormal then text:SetFontObject(ChatFontNormal) end;" +
               "text:SetPoint('TOPLEFT',frame,'TOPLEFT',10,-30);" +
               "text:SetPoint('BOTTOMRIGHT',frame,'BOTTOMRIGHT',-10,10);" +
               "text:SetJustifyH('LEFT');" +
               "text:SetJustifyV('TOP');" +
               "text:SetTextColor(0.82,0.95,0.82,1);" +
               "text:SetShadowOffset(1,-1);" +
               "text:SetShadowColor(0,0,0,1);" +
               "frame.TalosForgeText = text;" +
               "end;" +
               "frame:Show();" +
               "frame.TalosForgeText:SetText([[" + safe + "]]);";
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
