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
    private const int MinPublishIntervalMs = 1000;

    private readonly IUnlockerClient _unlockerClient;
    private readonly BotOptions _options;
    private long _lastPublishUnixMs;
    private bool _overlayVisible;

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

        if (!ShouldRender(snapshot))
        {
            if (!_overlayVisible)
            {
                return 0;
            }

            _overlayVisible = false;
            await SendLuaAsync(BuildHideLua(), tickId, cancellationToken).ConfigureAwait(false);
            return 1;
        }

        var interval = _options.InGameOverlayEveryTicks;
        var forcePublish = !_overlayVisible;
        if (!forcePublish && (interval <= 0 || tickId % interval != 0))
        {
            return 0;
        }

        var nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!forcePublish && nowUnixMs - _lastPublishUnixMs < MinPublishIntervalMs)
        {
            return 0;
        }

        _lastPublishUnixMs = nowUnixMs;
        _overlayVisible = true;

        var message = BuildOverlayMessage(tickId, state, snapshot, queuedCommands);
        await SendLuaAsync(BuildLua(message), nowUnixMs, cancellationToken).ConfigureAwait(false);
        return 1;
    }

    private async Task SendLuaAsync(string lua, long commandId, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { code = lua });
        var command = new UnlockerCommand(
            commandId,
            UnlockerOpcode.LuaDoString,
            payload,
            DateTimeOffset.UtcNow);

        await _unlockerClient.SendAsync(command, cancellationToken).ConfigureAwait(false);
    }

    internal static string BuildLua(string message)
    {
        var safe = (message ?? string.Empty).Replace("]]", "] ]");

        return "if not _G.TalosForgeDiag then _G.TalosForgeDiag = { lastLuaError = '', errorCount = 0, lastLuaErrorAt = '' } end;" +
               "if not _G.TalosForgeDiagInstalled and _G.seterrorhandler and _G.geterrorhandler then " +
               "local prev = geterrorhandler();" +
               "seterrorhandler(function(msg) " +
               "local diag = _G.TalosForgeDiag or {};" +
               "diag.lastLuaError = tostring(msg or '');" +
               "diag.errorCount = (tonumber(diag.errorCount) or 0) + 1;" +
               "diag.lastLuaErrorAt = (date and date('%H:%M:%S')) or '';" +
               "_G.TalosForgeDiag = diag;" +
               "if prev then return prev(msg) end;" +
               "end);" +
               "_G.TalosForgeDiagInstalled = true;" +
               "end;" +
               "local tfDiag = _G.TalosForgeDiag or {};" +
               "local tfDiagText = tostring(tfDiag.lastLuaError or '');" +
               "if string.len(tfDiagText) > 96 then tfDiagText = string.sub(tfDiagText,1,96) .. '...' end;" +
               "local tfSummary = [[" + safe + "]];" +
               "if tfDiagText ~= '' then tfSummary = tfSummary .. '\\nLuaErr: ' .. tfDiagText end;" +
               "local tfCanDraw = (_G.UnitExists and UnitExists('player'));" +
               "if not tfCanDraw then " +
               "if _G.TalosForgeStatusFrame then _G.TalosForgeStatusFrame:Hide() end;" +
               "else " +
               "local frame = _G['TalosForgeStatusFrame'];" +
               "if not frame then " +
               "frame = CreateFrame('Frame','TalosForgeStatusFrame',UIParent);" +
               "frame:SetSize(360,92);" +
               "frame:SetPoint('TOPLEFT',UIParent,'TOPLEFT',20,-140);" +
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
               "text:SetPoint('TOPLEFT',frame,'TOPLEFT',10,-30);" +
               "text:SetPoint('BOTTOMRIGHT',frame,'BOTTOMRIGHT',-10,10);" +
               "text:SetJustifyH('LEFT');" +
               "text:SetJustifyV('TOP');" +
               "text:SetTextColor(0.82,0.95,0.82,1);" +
               "text:SetShadowOffset(1,-1);" +
               "text:SetShadowColor(0,0,0,1);" +
               "frame.TalosForgeText = text;" +
               "end;" +
               "if frame.TalosForgeText then " +
               "if GameFontHighlightSmall then frame.TalosForgeText:SetFontObject(GameFontHighlightSmall) " +
               "elseif GameFontNormalSmall then frame.TalosForgeText:SetFontObject(GameFontNormalSmall) " +
               "elseif GameFontNormal then frame.TalosForgeText:SetFontObject(GameFontNormal) " +
               "elseif ChatFontNormal then frame.TalosForgeText:SetFontObject(ChatFontNormal) " +
               "elseif SystemFont_Shadow_Small then frame.TalosForgeText:SetFontObject(SystemFont_Shadow_Small) " +
               "elseif SystemFont_Small then frame.TalosForgeText:SetFontObject(SystemFont_Small) end; " +
               "if not frame.TalosForgeText:GetFont() then frame.TalosForgeText:SetFont('Fonts\\\\FRIZQT__.TTF',12,'') end; " +
               "end;" +
               "frame:Show();" +
               "if frame.TalosForgeText and frame.TalosForgeText:GetFont() then frame.TalosForgeText:SetText(tfSummary) end;" +
               "end;";
    }

    internal static string BuildHideLua()
    {
        return "if _G.TalosForgeStatusFrame then _G.TalosForgeStatusFrame:Hide() end;" +
               "if _G.TalosForgeBenchDiagFrame then _G.TalosForgeBenchDiagFrame:Hide() end;";
    }

    internal static bool ShouldRender(WorldSnapshot snapshot)
    {
        return snapshot.Success && snapshot.Player is not null;
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
