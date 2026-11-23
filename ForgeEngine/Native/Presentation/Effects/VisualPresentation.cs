using System;
using ForgeCore;
using ForgeEngine.Framework;
using ForgeVisual;
using UnityEngine;

namespace ForgeEngine.Presentation.Native;

/// <summary>
/// The production effect player: every member of `forge.action.presentation.effect_play` is one effect of ForgeVisual's
/// `Fx`, by the name <see cref="EffectMember.VisualName"/> (`Game:` for the game's own effects, `Forge:` for the bundle
/// Forge ships). ForgeVisual holds the pools, the instances, the sustained-effect windows and their per-frame step; this
/// class resolves each member once per step and turns the answer into the row's refusal code. A member ForgeVisual does not have,
/// or whose effect this world has not loaded, answers <see cref="ResultCode.LevelEventUnavailable"/> instead of playing nothing.
/// A sustained member holds one copy per play (<see cref="EffectStep"/>): the play's hold key is taken from `Fx.NewKey` the first
/// time its play (the host's play number, see `CommandContext.Play`) plays, and kept in the step's own table.
/// </summary>
internal sealed class VisualEffectPlayer
{
    private readonly Action<string> _report;
    private readonly OnceReporter _once = new();

    internal VisualEffectPlayer(Action<string> report) => _report = report;

    /// <summary>Plays one member on this machine. The answer is null when the effect ran, or the refusal code that says
    /// why it did not: the game has not loaded the effect yet, ForgeVisual has no such effect, or the step has more held plays
    /// than its table holds.</summary>
    public string? Play(EffectMember member, in EffectRequest request)
    {
        var effect = Resolve(member, request.Step);
        if (effect == null) return ResultCode.LevelEventUnavailable;
        FxResult result;
        if (member.NeedsEnd)
        {
            if (!effect.Sustained) result = Fx.Line(effect, 0, request.Position, request.End!.Value, true, Color.white);
            else if (KeyOf(request) is var key && key == 0) return ResultCode.EffectHeldBudget;
            else result = Fx.Line(effect, key, request.Position, request.End!.Value, true, Color.white);
        }
        else
        {
            var direction = request.Direction ?? Vector3.zero;
            if (!effect.Sustained) result = Fx.Play(effect, request.Position, direction, request.Size, Color.white);
            else if (KeyOf(request) is var key && key == 0) return ResultCode.EffectHeldBudget;
            else result = Fx.Hold(effect, key, request.Position, direction, request.Size, Color.white);
        }
        return result switch
        {
            FxResult.Played => null,
            FxResult.Unavailable => ResultCode.LevelEventUnavailable,
            _ => throw new InvalidOperationException(member.Id + " does not fit the ForgeVisual effect " + effect.Name + " it is played as."),
        };
    }

    /// <summary>This play's hold key, or 0 when its step's table has no room for it.</summary>
    private static int KeyOf(in EffectRequest request)
    {
        var at = request.Step.Take(request.World, request.Play, request.Tick);
        if (at < 0) return 0;
        ref var key = ref request.Step.KeyAt(at);
        if (key == 0) key = Fx.NewKey();
        return key;
    }

    private FxHandle? Resolve(EffectMember member, EffectStep step)
    {
        if (ReferenceEquals(step.Member, member) && step.Resolved is FxHandle known) return known;
        var handle = Fx.Find(member.VisualName);
        if (handle == null)
        {
            if (_once.ShouldReport(member.Id))
                _report("presentation.effect-missing: ForgeVisual has no effect " + member.VisualName + " (see its log for the reason); the member is refused");
            return null;
        }
        step.Member = member;
        step.Resolved = handle;
        return handle;
    }
}
