using TalosForge.Core.Plugins;

namespace TalosForge.Core.BehaviorTree.Leaves;

/// <summary>
/// Casts a spell by name. Returns Success after issuing the command,
/// Failure if preconditions aren't met (no target, out of range, etc.)
/// </summary>
public sealed class CastSpellNode : IBtNode
{
    private readonly string _spellName;
    private readonly float _maxRange;
    private readonly bool _requireTarget;

    public CastSpellNode(string name, string spellName, float maxRange = 30f, bool requireTarget = true)
    {
        Name = name;
        _spellName = spellName;
        _maxRange = maxRange;
        _requireTarget = requireTarget;
    }

    public string Name { get; }

    public async Task<BtNodeStatus> TickAsync(IBotContext context, CancellationToken ct)
    {
        if (context.Me == null || context.Me.IsCasting)
            return BtNodeStatus.Failure;

        if (_requireTarget)
        {
            var target = context.Target;
            if (target == null || target.IsDead)
                return BtNodeStatus.Failure;

            if (context.DistanceTo(target) > _maxRange)
                return BtNodeStatus.Failure;
        }

        await context.CastSpellAsync(_spellName, ct).ConfigureAwait(false);
        return BtNodeStatus.Success;
    }

    public void Reset() { }
}
