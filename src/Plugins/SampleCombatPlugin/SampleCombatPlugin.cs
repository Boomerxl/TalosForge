using TalosForge.Core.Abstractions;
using TalosForge.Core.Models;

namespace SampleCombatPlugin;

public sealed class SampleCombatPlugin : IPlugin
{
    private IPluginContext? _context;
    private ulong? _lastTarget;

    public string Name => "SampleCombatPlugin";
    public Version Version { get; } = new(1, 0, 0);

    public void Initialize(IPluginContext context)
    {
        _context = context;
    }

    public Task TickAsync(WorldSnapshot snapshot, IReadOnlyList<BotEvent> events, CancellationToken cancellationToken)
    {
        if (_context == null)
        {
            return Task.CompletedTask;
        }

        var targetGuid = snapshot.Player?.TargetGuid;
        if (targetGuid.HasValue && targetGuid != 0 && targetGuid != _lastTarget)
        {
            var command = new UnlockerCommand(
                commandId: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Opcode: UnlockerOpcode.LuaDoString,
                PayloadJson: "{\"code\":\"print('TalosForge plugin pulse')\"}",
                TimestampUtc: DateTimeOffset.UtcNow);
            _context.QueueCommand(command);
            _lastTarget = targetGuid;
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _context = null;
    }
}
