using System.Text.Json;
using TalosForge.UnlockerAgentHost.Models;
using Xunit;

namespace TalosForge.Tests.UnlockerAgentHost;

public sealed class AgentPipeProtocolTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void AgentPipeRequest_Serializes_With_Expected_Fields()
    {
        var request = new AgentPipeRequest(
            Version: 1,
            CommandId: 123,
            Opcode: "LuaDoString",
            OpcodeValue: 1,
            PayloadJson: "{\"code\":\"print('hi')\"}",
            TimestampUnixMs: 11,
            RequestTimeoutMs: 2222,
            EvasionProfile: "full");

        var json = JsonSerializer.Serialize(request, JsonOptions);

        Assert.Contains("\"commandId\":123", json, StringComparison.Ordinal);
        Assert.Contains("\"requestTimeoutMs\":2222", json, StringComparison.Ordinal);
        Assert.Contains("\"evasionProfile\":\"full\"", json, StringComparison.Ordinal);
    }
}
