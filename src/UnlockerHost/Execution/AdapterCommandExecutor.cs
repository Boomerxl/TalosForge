using System.Text.Json;
using TalosForge.Core.Models;
using TalosForge.UnlockerHost.Abstractions;
using TalosForge.UnlockerHost.Models;

namespace TalosForge.UnlockerHost.Execution;

/// <summary>
/// Validation-first executor that normalizes command payloads before backend dispatch.
/// </summary>
public sealed class AdapterCommandExecutor : ICommandExecutor
{
    private readonly IAdapterBackend _backend;

    public AdapterCommandExecutor(IAdapterBackend backend)
    {
        _backend = backend;
    }

    public async ValueTask<CommandExecutionResult> ExecuteAsync(UnlockerCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryValidateAndNormalizePayload(
                command.Opcode,
                command.PayloadJson,
                out var normalizedPayload,
                out var failureCode,
                out var error))
        {
            return CommandExecutionResult.Fail(
                $"{failureCode}: {error}",
                BuildCodePayload(failureCode, error ?? "Invalid payload."));
        }

        var normalizedCommand = command with { PayloadJson = normalizedPayload };
        try
        {
            var backendResult = await _backend.ExecuteAsync(normalizedCommand, cancellationToken).ConfigureAwait(false);
            if (backendResult.Success)
            {
                return backendResult.PayloadJson is null
                    ? backendResult with
                    {
                        PayloadJson = BuildCodePayload(
                            AdapterResultCodes.Ok,
                            "Command validated and executed by adapter backend.")
                    }
                    : backendResult;
            }

            return backendResult.PayloadJson is null
                ? backendResult with
                {
                    PayloadJson = BuildCodePayload(
                        AdapterResultCodes.BackendError,
                        backendResult.Message)
                }
                : backendResult;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CommandExecutionResult.Fail(
                $"{AdapterResultCodes.BackendError}: {ex.GetType().Name}",
                BuildCodePayload(AdapterResultCodes.BackendError, ex.Message));
        }
    }

    private static bool TryValidateAndNormalizePayload(
        UnlockerOpcode opcode,
        string payloadJson,
        out string normalizedPayloadJson,
        out string failureCode,
        out string? error)
    {
        normalizedPayloadJson = "{}";
        failureCode = AdapterResultCodes.InvalidPayload;
        error = null;

        JsonDocument payloadDocument;
        try
        {
            payloadDocument = JsonDocument.Parse(string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson);
        }
        catch (JsonException ex)
        {
            error = $"Payload must be valid JSON ({ex.Message}).";
            return false;
        }

        using (payloadDocument)
        {
            var root = payloadDocument.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "Payload root must be a JSON object.";
                return false;
            }

            switch (opcode)
            {
                case UnlockerOpcode.LuaDoString:
                    return TryNormalizeLua(root, out normalizedPayloadJson, out error);
                case UnlockerOpcode.CastSpellByName:
                    return TryNormalizeCast(root, out normalizedPayloadJson, out error);
                case UnlockerOpcode.SetTargetGuid:
                    return TryNormalizeTargetGuid(root, out normalizedPayloadJson, out error);
                case UnlockerOpcode.Face:
                    return TryNormalizeFace(root, out normalizedPayloadJson, out error);
                case UnlockerOpcode.MoveTo:
                    return TryNormalizeMoveTo(root, out normalizedPayloadJson, out error);
                case UnlockerOpcode.Interact:
                    return TryNormalizeInteract(root, out normalizedPayloadJson, out error);
                case UnlockerOpcode.Stop:
                    return TryNormalizeStop(root, out normalizedPayloadJson, out error);
                default:
                    error = $"{AdapterResultCodes.UnsupportedOpcode}: {(int)opcode}";
                    failureCode = AdapterResultCodes.UnsupportedOpcode;
                    return false;
            }
        }
    }

    private static bool TryNormalizeLua(JsonElement root, out string normalizedPayloadJson, out string? error)
    {
        normalizedPayloadJson = "{}";
        error = null;

        if (!TryGetRequiredString(root, "code", out var code, out error, strictPropertyCount: 1))
        {
            return false;
        }

        if (code.Length > 32_768)
        {
            error = "Property 'code' exceeds max length (32768).";
            return false;
        }

        normalizedPayloadJson = JsonSerializer.Serialize(new { code });
        return true;
    }

    private static bool TryNormalizeCast(JsonElement root, out string normalizedPayloadJson, out string? error)
    {
        normalizedPayloadJson = "{}";
        error = null;

        if (!TryGetRequiredString(root, "spell", out var spell, out error, strictPropertyCount: 1))
        {
            return false;
        }

        if (spell.Length > 128)
        {
            error = "Property 'spell' exceeds max length (128).";
            return false;
        }

        normalizedPayloadJson = JsonSerializer.Serialize(new { spell });
        return true;
    }

    private static bool TryNormalizeTargetGuid(JsonElement root, out string normalizedPayloadJson, out string? error)
    {
        normalizedPayloadJson = "{}";
        error = null;

        if (!TryGetProperty(root, "guid", out var guidElement, strictPropertyCount: 1, out error))
        {
            return false;
        }

        if (!TryParseGuid(guidElement, out var guid))
        {
            error = "Property 'guid' must be an unsigned integer or hex string.";
            return false;
        }

        normalizedPayloadJson = JsonSerializer.Serialize(new { guid });
        return true;
    }

    private static bool TryNormalizeFace(JsonElement root, out string normalizedPayloadJson, out string? error)
    {
        normalizedPayloadJson = "{}";
        error = null;

        if (!TryGetProperty(root, "facing", out var facingElement, strictPropertyCount: 2, out error) ||
            !TryGetProperty(root, "smoothing", out var smoothingElement, strictPropertyCount: 2, out error))
        {
            return false;
        }

        if (!TryGetFiniteSingle(facingElement, out var facing))
        {
            error = "Property 'facing' must be a finite number.";
            return false;
        }

        if (!TryGetFiniteSingle(smoothingElement, out var smoothing))
        {
            error = "Property 'smoothing' must be a finite number.";
            return false;
        }

        normalizedPayloadJson = JsonSerializer.Serialize(new { facing, smoothing });
        return true;
    }

    private static bool TryNormalizeMoveTo(JsonElement root, out string normalizedPayloadJson, out string? error)
    {
        normalizedPayloadJson = "{}";
        error = null;

        if (!TryGetProperty(root, "x", out var xElement, strictPropertyCount: 4, out error) ||
            !TryGetProperty(root, "y", out var yElement, strictPropertyCount: 4, out error) ||
            !TryGetProperty(root, "z", out var zElement, strictPropertyCount: 4, out error) ||
            !TryGetProperty(root, "overshootThreshold", out var overshootElement, strictPropertyCount: 4, out error))
        {
            return false;
        }

        if (!TryGetFiniteSingle(xElement, out var x) ||
            !TryGetFiniteSingle(yElement, out var y) ||
            !TryGetFiniteSingle(zElement, out var z))
        {
            error = "Properties 'x', 'y', and 'z' must be finite numbers.";
            return false;
        }

        if (!TryGetFiniteSingle(overshootElement, out var overshootThreshold) || overshootThreshold < 0f)
        {
            error = "Property 'overshootThreshold' must be a finite number >= 0.";
            return false;
        }

        normalizedPayloadJson = JsonSerializer.Serialize(new { x, y, z, overshootThreshold });
        return true;
    }

    private static bool TryNormalizeInteract(JsonElement root, out string normalizedPayloadJson, out string? error)
    {
        normalizedPayloadJson = "{}";
        error = null;

        var propertyCount = CountProperties(root);
        if (propertyCount == 0)
        {
            return true;
        }

        if (!TryGetProperty(root, "guid", out var guidElement, strictPropertyCount: 1, out error))
        {
            return false;
        }

        if (!TryParseGuid(guidElement, out var guid))
        {
            error = "Property 'guid' must be an unsigned integer or hex string.";
            return false;
        }

        normalizedPayloadJson = JsonSerializer.Serialize(new { guid });
        return true;
    }

    private static bool TryNormalizeStop(JsonElement root, out string normalizedPayloadJson, out string? error)
    {
        normalizedPayloadJson = "{}";
        error = null;

        if (CountProperties(root) != 0)
        {
            error = "Stop payload must be an empty object.";
            return false;
        }

        return true;
    }

    private static bool TryGetRequiredString(
        JsonElement root,
        string propertyName,
        out string value,
        out string? error,
        int strictPropertyCount)
    {
        value = string.Empty;
        if (!TryGetProperty(root, propertyName, out var property, strictPropertyCount, out error))
        {
            return false;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            error = $"Property '{propertyName}' must be a string.";
            return false;
        }

        var parsed = property.GetString();
        if (string.IsNullOrWhiteSpace(parsed))
        {
            error = $"Property '{propertyName}' is required.";
            return false;
        }

        value = parsed.Trim();
        return true;
    }

    private static bool TryGetProperty(
        JsonElement root,
        string propertyName,
        out JsonElement value,
        int strictPropertyCount,
        out string? error)
    {
        value = default;
        error = null;

        var actualCount = CountProperties(root);
        if (actualCount != strictPropertyCount)
        {
            error = $"Payload must contain exactly {strictPropertyCount} properties.";
            return false;
        }

        if (!root.TryGetProperty(propertyName, out value))
        {
            error = $"Missing required property '{propertyName}'.";
            return false;
        }

        return true;
    }

    private static bool TryGetFiniteSingle(JsonElement element, out float value)
    {
        value = 0f;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetSingle(out value))
        {
            return false;
        }

        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static bool TryParseGuid(JsonElement element, out ulong guid)
    {
        guid = 0UL;
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return element.TryGetUInt64(out guid);
            case JsonValueKind.String:
            {
                var raw = element.GetString();
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return false;
                }

                raw = raw.Trim();
                if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    return ulong.TryParse(raw[2..], System.Globalization.NumberStyles.HexNumber, null, out guid);
                }

                return ulong.TryParse(raw, out guid);
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

    public static string BuildCodePayload(string code, string message)
    {
        return JsonSerializer.Serialize(new
        {
            code,
            message,
        });
    }
}
