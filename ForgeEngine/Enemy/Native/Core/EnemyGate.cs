using ForgeEngine.Framework;

namespace ForgeEnemy.Native;

/// <summary>The subscription gate of one binding of this provider, or a gate that never has subscribers when the
/// binding was withdrawn with its function (<see cref="EnemyHookAvailability"/>). A fact's publisher asks the gate
/// exactly as before, so a withdrawn trigger costs one field read and publishes nothing.</summary>
internal sealed class EnemyGate
{
    private readonly RuntimeSubscriptionGate? _gate;

    internal EnemyGate(string bindingId, RuntimeSubscriptionGate? gate) { BindingId = bindingId; _gate = gate; }

    public string BindingId { get; }
    public bool HasSubscribers => _gate != null && _gate.HasSubscribers;

    /// <summary>Begins one event of this binding, written by its trigger's output positions. Asked only after
    /// <see cref="HasSubscribers"/> said yes, so a withdrawn binding never reaches it.</summary>
    internal EventWriter Begin(RuntimeModuleHandle module) => module.BeginEvent(_gate!);

    /// <summary>Begins one committed fact of this binding on the command in progress. A withdrawn binding is not
    /// registered, so it carries no fact and the answer is false.</summary>
    internal bool TryBeginFact(CommandContext context, out FactWriter fact)
    {
        fact = _gate == null ? default : context.BeginFact(_gate);
        return _gate != null;
    }
}
