#include "LuaFrameOverlay.h"

namespace TalosForge::LuaFrameOverlay {

const char* GetCreateFrameScript() {
    // Keep in sync with TalosForge.Core.Drawing.TalosForgeFrameLua.CreateHub().
    return R"TF_OVERLAY(
if not _G.TalosForgeDiag then _G.TalosForgeDiag = { lastLuaError = '', errorCount = 0, lastLuaErrorAt = '' } end
if not _G.TalosForgeDiagInstalled and _G.seterrorhandler and _G.geterrorhandler then
  local prev = geterrorhandler()
  seterrorhandler(function(msg)
    local diag = _G.TalosForgeDiag or {}
    diag.lastLuaError = tostring(msg or '')
    diag.errorCount = (tonumber(diag.errorCount) or 0) + 1
    diag.lastLuaErrorAt = (date and date('%H:%M:%S')) or ''
    _G.TalosForgeDiag = diag
    if prev then return prev(msg) end
  end)
  _G.TalosForgeDiagInstalled = true
end

if TalosForgeHub then return end
_G.TalosForgeUI = _G.TalosForgeUI or {}
local U = _G.TalosForgeUI

local f = CreateFrame('Frame', 'TalosForgeHub', UIParent)
TalosForgeHub = f
f:SetWidth(400)
f:SetHeight(460)
f:SetPoint('TOPRIGHT', UIParent, 'TOPRIGHT', -20, -80)
f:SetFrameStrata('DIALOG')
f:SetBackdrop({
  bgFile='Interface\\DialogFrame\\UI-DialogBox-Background',
  edgeFile='Interface\\DialogFrame\\UI-DialogBox-Border',
  tile=true, tileSize=16, edgeSize=16,
  insets={left=4,right=4,top=4,bottom=4}
})
f:SetBackdropColor(0, 0, 0, 0.88)
f:EnableMouse(true)
f:SetMovable(true)
f:RegisterForDrag('LeftButton')
f:SetScript('OnDragStart', function(self) self:StartMoving() end)
f:SetScript('OnDragStop', function(self) self:StopMovingOrSizing() end)

local title = f:CreateFontString(nil, 'OVERLAY', 'GameFontNormalLarge')
title:SetPoint('TOP', 0, -8)
title:SetText('TalosForge')
title:SetTextColor(0.4, 0.85, 1.0)

local closeBtn = CreateFrame('Button', nil, f, 'UIPanelCloseButton')
closeBtn:SetPoint('TOPRIGHT', -4, -4)
closeBtn:SetScript('OnClick', function() f:Hide() end)

local tabNames = { 'Debug', 'Plugins' }
U.tabButtons = {}
local tabY = -32
for i=1,2 do
  local btn = CreateFrame('Button', 'TFHubTab'..i, f, 'UIPanelButtonTemplate')
  btn:SetWidth(180)
  btn:SetHeight(22)
  btn:SetPoint('TOPLEFT', f, 'TOPLEFT', 12 + (i-1)*184, tabY)
  btn:SetText(tabNames[i])
  U.tabButtons[i] = btn
end

local bodyTop = -58
U.panels = {}

local function makeLineScroll(parent, name, lineCount)
  local panel = CreateFrame('Frame', name, parent)
  panel:SetPoint('TOPLEFT', 8, bodyTop)
  panel:SetPoint('BOTTOMRIGHT', parent, 'BOTTOMRIGHT', -8, 10)
  local scroll = CreateFrame('ScrollFrame', name..'Scroll', panel, 'UIPanelScrollFrameTemplate')
  scroll:SetPoint('TOPLEFT', 0, 0)
  scroll:SetPoint('BOTTOMRIGHT', -28, 0)
  local content = CreateFrame('Frame', name..'Content', scroll)
  content:SetWidth(330)
  content:SetHeight(math.max(400, lineCount * 14 + 20))
  scroll:SetScrollChild(content)
  local lines = {}
  for j=1,lineCount do
    local line = content:CreateFontString(nil, 'OVERLAY', 'GameFontHighlightSmall')
    line:SetPoint('TOPLEFT', 6, -(j-1)*13 - 4)
    line:SetWidth(310)
    line:SetJustifyH('LEFT')
    line:SetText('')
    lines[j] = line
  end
  return panel, lines
end

U.panels[1], U.lines_debug = makeLineScroll(f, 'TFPanelDebug', 28)
U.panels[2], U.lines_plugins = makeLineScroll(f, 'TFPanelPlugins', 16)

local function showTab(idx)
  for i=1,2 do
    U.panels[i]:SetShown(i == idx)
  end
  U.activeTab = idx
end

for i=1,2 do
  U.tabButtons[i]:SetScript('OnClick', function() showTab(i) end)
end
showTab(1)

f:Show()
)TF_OVERLAY";
}

} // namespace TalosForge::LuaFrameOverlay
