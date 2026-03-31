using TalosForge.Core.BehaviorTree.Composites;
using TalosForge.Core.BehaviorTree.Decorators;
using TalosForge.Core.BehaviorTree.Leaves;
using TalosForge.Core.Plugins;

namespace TalosForge.Core.BehaviorTree;

/// <summary>
/// Fluent builder for constructing behavior trees in code.
/// </summary>
public sealed class BehaviorTreeBuilder
{
    private readonly Stack<(string Type, string Name, List<IBtNode> Children)> _stack = new();
    private IBtNode? _root;

    public BehaviorTreeBuilder Selector(string name)
    {
        _stack.Push(("Selector", name, new List<IBtNode>()));
        return this;
    }

    public BehaviorTreeBuilder Sequence(string name)
    {
        _stack.Push(("Sequence", name, new List<IBtNode>()));
        return this;
    }

    public BehaviorTreeBuilder Condition(string name, Func<IBotContext, bool> predicate)
    {
        _stack.Push(("Condition", name, new List<IBtNode>()));
        _stack.Peek().Children.Add(new CheckNode($"{name}_check", predicate));
        return this;
    }

    public BehaviorTreeBuilder CastSpell(string spellName, float range = 30f, bool requireTarget = true)
    {
        AddLeaf(new CastSpellNode($"Cast_{spellName}", spellName, range, requireTarget));
        return this;
    }

    public BehaviorTreeBuilder Check(string name, Func<IBotContext, bool> predicate)
    {
        AddLeaf(new CheckNode(name, predicate));
        return this;
    }

    public BehaviorTreeBuilder Action(string name, Func<IBotContext, CancellationToken, Task<BtNodeStatus>> action)
    {
        AddLeaf(new ActionNode(name, action));
        return this;
    }

    public BehaviorTreeBuilder Wait(string name, TimeSpan duration)
    {
        AddLeaf(new WaitNode(name, duration));
        return this;
    }

    public BehaviorTreeBuilder End()
    {
        if (_stack.Count == 0)
            throw new InvalidOperationException("No open composite to close.");

        var (type, name, children) = _stack.Pop();

        IBtNode node = type switch
        {
            "Selector" => new SelectorNode(name, children),
            "Sequence" => new SequenceNode(name, children),
            "Condition" => new ConditionNode(name, _ => true, children.Count > 1 ? children[1] : children[0]),
            _ => throw new InvalidOperationException($"Unknown composite type: {type}"),
        };

        if (_stack.Count > 0)
        {
            _stack.Peek().Children.Add(node);
        }
        else
        {
            _root = node;
        }

        return this;
    }

    public IBtNode Build()
    {
        if (_stack.Count > 0)
            throw new InvalidOperationException($"Unclosed composites remain: {_stack.Count}");

        return _root ?? throw new InvalidOperationException("No root node was built.");
    }

    private void AddLeaf(IBtNode leaf)
    {
        if (_stack.Count > 0)
        {
            _stack.Peek().Children.Add(leaf);
        }
        else
        {
            _root = leaf;
        }
    }
}
