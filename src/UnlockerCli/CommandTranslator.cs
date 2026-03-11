using System.Globalization;

namespace TalosForge.UnlockerCli;

public static class CommandTranslator
{
    public static bool TryTranslate(
        string verb,
        IReadOnlyList<string> args,
        out UnlockerCliCommand command,
        out string error)
    {
        command = default;
        error = string.Empty;

        switch (verb.Trim().ToLowerInvariant())
        {
            case "lua":
                if (args.Count < 1)
                {
                    error = "lua verb requires one argument: lua <code>";
                    return false;
                }

                command = new UnlockerCliCommand(args[0], "ACK:LuaDoString");
                return true;

            case "cast":
                if (args.Count < 1)
                {
                    error = "cast verb requires one argument: cast <spell>";
                    return false;
                }

                command = new UnlockerCliCommand(
                    $"CastSpellByName('{EscapeLuaString(args[0])}')",
                    "ACK:CastSpellByName");
                return true;

            case "target":
                if (args.Count < 1)
                {
                    error = "target verb requires one argument: target <guid>";
                    return false;
                }

                command = new UnlockerCliCommand(
                    BuildTargetLua(args[0]),
                    "ACK:SetTargetGuid");
                return true;

            case "face":
                if (args.Count < 2 ||
                    !TryParseFloat(args[0], out var facing) ||
                    !TryParseFloat(args[1], out var smoothing))
                {
                    error = "face verb requires finite numbers: face <facing> <smoothing>";
                    return false;
                }

                command = new UnlockerCliCommand(
                    BuildFaceLua(facing, smoothing),
                    "ACK:Face");
                return true;

            case "moveto":
                if (args.Count < 4 ||
                    !TryParseFloat(args[0], out var x) ||
                    !TryParseFloat(args[1], out var y) ||
                    !TryParseFloat(args[2], out var z) ||
                    !TryParseFloat(args[3], out var overshoot))
                {
                    error = "moveto verb requires finite numbers: moveto <x> <y> <z> <overshootThreshold>";
                    return false;
                }

                command = new UnlockerCliCommand(
                    BuildMoveToLua(x, y, z, overshoot),
                    "ACK:MoveTo");
                return true;

            case "interact":
                command = args.Count == 0
                    ? new UnlockerCliCommand(
                        "if _G.Interact then Interact() elseif _G.InteractUnit then InteractUnit('target') else error('Interact unavailable') end",
                        "ACK:Interact")
                    : new UnlockerCliCommand(
                        BuildInteractLua(args[0]),
                        "ACK:Interact");
                return true;

            case "stop":
                command = new UnlockerCliCommand(
                    "if _G.Stop then Stop() else if _G.MoveForwardStop then MoveForwardStop() end if _G.MoveBackwardStop then MoveBackwardStop() end if _G.StrafeLeftStop then StrafeLeftStop() end if _G.StrafeRightStop then StrafeRightStop() end if _G.TurnLeftStop then TurnLeftStop() end if _G.TurnRightStop then TurnRightStop() end end",
                    "ACK:Stop");
                return true;

            default:
                error = $"Unsupported verb '{verb}'.";
                return false;
        }
    }

    private static string BuildTargetLua(string guid)
    {
        var safeGuid = EscapeLuaString(guid);
        return $"if _G.SetTargetGuid then SetTargetGuid('{safeGuid}') elseif _G.TargetByGuid then TargetByGuid('{safeGuid}') elseif _G.TargetGuid then TargetGuid('{safeGuid}') else error('SetTargetGuid unavailable') end";
    }

    private static string BuildFaceLua(float facing, float smoothing)
    {
        var facingRaw = facing.ToString("0.######", CultureInfo.InvariantCulture);
        var smoothingRaw = smoothing.ToString("0.######", CultureInfo.InvariantCulture);
        return $"if _G.Face then Face({facingRaw},{smoothingRaw}) elseif _G.SetFacing then SetFacing({facingRaw},{smoothingRaw}) else error('Face unavailable') end";
    }

    private static string BuildMoveToLua(float x, float y, float z, float overshoot)
    {
        var xRaw = x.ToString("0.######", CultureInfo.InvariantCulture);
        var yRaw = y.ToString("0.######", CultureInfo.InvariantCulture);
        var zRaw = z.ToString("0.######", CultureInfo.InvariantCulture);
        var overshootRaw = overshoot.ToString("0.######", CultureInfo.InvariantCulture);
        return $"if _G.MoveTo then MoveTo({xRaw},{yRaw},{zRaw},{overshootRaw}) elseif _G.ClickToMove then ClickToMove({xRaw},{yRaw},{zRaw}) else error('MoveTo unavailable') end";
    }

    private static string BuildInteractLua(string guid)
    {
        var safeGuid = EscapeLuaString(guid);
        return $"if _G.Interact then Interact('{safeGuid}') elseif _G.InteractGuid then InteractGuid('{safeGuid}') else error('InteractGuid unavailable') end";
    }

    private static bool TryParseFloat(string raw, out float value)
    {
        if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static string EscapeLuaString(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);
    }
}

public readonly record struct UnlockerCliCommand(string LuaCode, string AckMessage);
