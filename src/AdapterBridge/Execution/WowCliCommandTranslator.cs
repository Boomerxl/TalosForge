using System.Globalization;
using System.Text.Json;
using TalosForge.AdapterBridge.Models;

namespace TalosForge.AdapterBridge.Execution;

public sealed record WowCliInvocation(
    string Verb,
    IReadOnlyList<string> Arguments,
    string AckMessage);

public static class WowCliCommandTranslator
{
    public static bool TryTranslate(
        AdapterPipeRequest request,
        out WowCliInvocation? invocation,
        out AdapterPipeResponse errorResponse)
    {
        invocation = null;
        errorResponse = new AdapterPipeResponse(true, string.Empty);

        if (!TryParsePayloadObject(request.PayloadJson, out var payload, out var payloadError))
        {
            errorResponse = Error("BRIDGE_WOWCLI_INVALID_PAYLOAD", payloadError!);
            return false;
        }

        switch (request.Opcode?.Trim().ToLowerInvariant())
        {
            case "luadostring":
                if (!TryGetRequiredString(payload, "code", out var code, out var luaError))
                {
                    errorResponse = Error("BRIDGE_WOWCLI_INVALID_PAYLOAD", luaError!);
                    return false;
                }

                invocation = new WowCliInvocation("lua", new[] { code }, "ACK:LuaDoString");
                return true;

            case "castspellbyname":
                if (!TryGetRequiredString(payload, "spell", out var spell, out var castError))
                {
                    errorResponse = Error("BRIDGE_WOWCLI_INVALID_PAYLOAD", castError!);
                    return false;
                }

                invocation = new WowCliInvocation("cast", new[] { spell }, "ACK:CastSpellByName");
                return true;

            case "settargetguid":
                if (!TryGetRequiredGuid(payload, "guid", out var guid, out var targetError))
                {
                    errorResponse = Error("BRIDGE_WOWCLI_INVALID_PAYLOAD", targetError!);
                    return false;
                }

                invocation = new WowCliInvocation("target", new[] { guid.ToString(CultureInfo.InvariantCulture) }, "ACK:SetTargetGuid");
                return true;

            case "face":
                if (!TryGetRequiredNumber(payload, "facing", out var facing, out var facingError) ||
                    !TryGetRequiredNumber(payload, "smoothing", out var smoothing, out facingError))
                {
                    errorResponse = Error("BRIDGE_WOWCLI_INVALID_PAYLOAD", facingError!);
                    return false;
                }

                invocation = new WowCliInvocation(
                    "face",
                    new[]
                    {
                        FormatNumber(facing),
                        FormatNumber(smoothing)
                    },
                    "ACK:Face");
                return true;

            case "moveto":
                if (!TryGetRequiredNumber(payload, "x", out var x, out var moveError) ||
                    !TryGetRequiredNumber(payload, "y", out var y, out moveError) ||
                    !TryGetRequiredNumber(payload, "z", out var z, out moveError) ||
                    !TryGetRequiredNumber(payload, "overshootThreshold", out var overshoot, out moveError))
                {
                    errorResponse = Error("BRIDGE_WOWCLI_INVALID_PAYLOAD", moveError!);
                    return false;
                }

                invocation = new WowCliInvocation(
                    "moveto",
                    new[]
                    {
                        FormatNumber(x),
                        FormatNumber(y),
                        FormatNumber(z),
                        FormatNumber(overshoot)
                    },
                    "ACK:MoveTo");
                return true;

            case "interact":
                if (TryGetOptionalGuid(payload, "guid", out var interactGuid, out var interactError))
                {
                    invocation = interactGuid.HasValue
                        ? new WowCliInvocation(
                            "interact",
                            new[] { interactGuid.Value.ToString(CultureInfo.InvariantCulture) },
                            "ACK:Interact")
                        : new WowCliInvocation("interact", Array.Empty<string>(), "ACK:Interact");
                    return true;
                }

                errorResponse = Error("BRIDGE_WOWCLI_INVALID_PAYLOAD", interactError!);
                return false;

            case "stop":
                if (CountProperties(payload) != 0)
                {
                    errorResponse = Error("BRIDGE_WOWCLI_INVALID_PAYLOAD", "Stop payload must be an empty object.");
                    return false;
                }

                invocation = new WowCliInvocation("stop", Array.Empty<string>(), "ACK:Stop");
                return true;

            default:
                errorResponse = Error(
                    "BRIDGE_WOWCLI_UNSUPPORTED_OPCODE",
                    $"Unsupported opcode '{request.Opcode}' ({request.OpcodeValue}).");
                return false;
        }
    }

    private static bool TryParsePayloadObject(
        string payloadJson,
        out JsonElement root,
        out string? error)
    {
        error = null;
        root = default;

        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson);
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            error = $"Payload is not valid JSON ({ex.Message}).";
            return false;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "Payload root must be a JSON object.";
            return false;
        }

        return true;
    }

    private static bool TryGetRequiredString(
        JsonElement root,
        string name,
        out string value,
        out string? error)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var property))
        {
            error = $"Missing required property '{name}'.";
            return false;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            error = $"Property '{name}' must be a string.";
            return false;
        }

        var parsed = property.GetString();
        if (string.IsNullOrWhiteSpace(parsed))
        {
            error = $"Property '{name}' cannot be empty.";
            return false;
        }

        value = parsed.Trim();
        error = null;
        return true;
    }

    private static bool TryGetRequiredNumber(
        JsonElement root,
        string name,
        out float value,
        out string? error)
    {
        value = 0f;
        if (!root.TryGetProperty(name, out var property))
        {
            error = $"Missing required property '{name}'.";
            return false;
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetSingle(out value) ||
            float.IsNaN(value) || float.IsInfinity(value))
        {
            error = $"Property '{name}' must be a finite number.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryGetRequiredGuid(
        JsonElement root,
        string name,
        out ulong guid,
        out string? error)
    {
        guid = 0;
        if (!root.TryGetProperty(name, out var property))
        {
            error = $"Missing required property '{name}'.";
            return false;
        }

        if (!TryParseGuid(property, out guid))
        {
            error = $"Property '{name}' must be a uint64 or hex string.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryGetOptionalGuid(
        JsonElement root,
        string name,
        out ulong? guid,
        out string? error)
    {
        guid = null;
        error = null;

        if (!root.TryGetProperty(name, out var property))
        {
            return CountProperties(root) == 0;
        }

        if (!TryParseGuid(property, out var parsed))
        {
            error = $"Property '{name}' must be a uint64 or hex string.";
            return false;
        }

        guid = parsed;
        return true;
    }

    private static bool TryParseGuid(JsonElement property, out ulong guid)
    {
        guid = 0;
        switch (property.ValueKind)
        {
            case JsonValueKind.Number:
                return property.TryGetUInt64(out guid);
            case JsonValueKind.String:
            {
                var raw = property.GetString();
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return false;
                }

                raw = raw.Trim();
                if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    return ulong.TryParse(raw[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out guid);
                }

                return ulong.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out guid);
            }
            default:
                return false;
        }
    }

    private static int CountProperties(JsonElement root)
    {
        var count = 0;
        foreach (var _ in root.EnumerateObject())
        {
            count++;
        }

        return count;
    }

    private static string FormatNumber(float value)
    {
        var rounded = MathF.Round(value, 6, MidpointRounding.AwayFromZero);
        return rounded.ToString("0.######", CultureInfo.InvariantCulture);
    }

    private static AdapterPipeResponse Error(string code, string message)
    {
        return new AdapterPipeResponse(false, $"{code}: {message}", Code: code);
    }
}
