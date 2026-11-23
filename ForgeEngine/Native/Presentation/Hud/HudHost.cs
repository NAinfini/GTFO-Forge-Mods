using System;
using System.Collections.Generic;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;

namespace ForgeEngine.Presentation.Native;

/// <summary>
/// The host side of the two HUD text rows (`hud_message`, `hud_value`): their bodies, the retained-line recipient
/// state and the once-per-tick language refresh that runs on the presentation provider's
/// frame. The provider's host (<see cref="PresentationHost"/>) creates one of these and forwards its lifecycle.
/// </summary>
internal sealed class HudHost : IDisposable
{
    private readonly RuntimeKernel _kernel;
    private readonly HudMessageActions _message;
    private readonly HudValueActions _value;
    private readonly IPlayerDirectory _players;
    private readonly Action<string> _report;
    private readonly HashSet<(string Site, string Code)> _optionalRefusals = new();
    private HudMessageRecipientState? _state;
    private HudScreenRecipientState? _screenState;
    // The frame steps are built once: a method-group argument would allocate a delegate on every tick.
    private Action? _stateFrame, _screenFrame;
    private readonly Action _messageLanguage, _valueLanguage;

    internal HudHost(RuntimeKernel kernel, IPlayerDirectory players, Action<string> report)
    {
        _kernel = kernel;
        _players = players;
        _report = report;
        var language = new HudLanguage();
        _message = new HudMessageActions(language, report);
        _value = new HudValueActions(kernel, language, players, report);
        _messageLanguage = _message.TickLanguage;
        _valueLanguage = _value.TickLanguage;
    }

    /// <summary>Adds the two rows' bodies to the provider's handler table.</summary>
    internal void AddHandlers(IDictionary<string, CommandHandler> handlers)
    {
        handlers[PresentationContract.HudMessageHandler] = context =>
        {
            var result = _message.Handle(context);
            _state?.Acknowledge(context, result);
            return result;
        };
        handlers[PresentationContract.HudValueHandler] = context =>
        {
            var result = _value.Handle(context);
            _screenState?.Acknowledge(context, result);
            return result;
        };
    }

    /// <summary>Called once the provider's registration exists: the retained-line state joins Runtime's recipient
    /// state.</summary>
    internal void Attach()
    {
        _state = new HudMessageRecipientState(_kernel, _players, _message, _report);
        _stateFrame = _state.Frame;
        _screenState = new HudScreenRecipientState(_kernel, _players, _value, _report);
        _screenFrame = _screenState.Frame;
    }

    internal void OnLifecycle(RuntimeLifecycleEvent change)
    {
        if (change.Kind == RuntimeLifecycleKind.CheckpointRestored)
        {
            _state?.CheckpointRestored();
            // A loaded game starts with no placed element: whatever should stand there is shown again by the behaviour.
            _value.ClearScreen();
            _screenState?.Clear();
        }
        if (change.Kind == RuntimeLifecycleKind.WorldChanged
            || change.Current.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped)
        {
            _message.Clear();
            _state?.Clear();
            _screenState?.Clear();
        }
    }

    /// <summary>Once per kernel tick of a world (the provider's per-tick <see cref="DomainWorkPhases"/> entry), so a
    /// paused or migrating kernel, which does not advance, is not driven.</summary>
    internal void Frame()
    {
        Step(_stateFrame, "hud-message.recovery");
        Step(_screenFrame, "hud-value.recovery");
        Step(_messageLanguage, "hud-message.language");
        Step(_valueLanguage, "hud-value.language");
    }

    /// <summary>One frame step. A missing player service is an install fact, reported once; anything else is
    /// contained and reported by site.</summary>
    private void Step(Action? step, string site)
    {
        if (step == null) return;
        try { step(); }
        catch (RuntimeContractException error) when (error.Code is PlayerDirectoryService.MissingCode
            or PlayerTextLanguageService.MissingCode or ResultCode.TeammateOverheadMissing)
        {
            if (!_optionalRefusals.Add((site, error.Code))) return;
            _report("presentation.hud-service-unresolved: site=" + site + "; code=" + error.Code);
        }
        catch (Exception error) { PresentationLog.Contained(site, error); }
    }

    public void Dispose()
    {
        _state?.Dispose();
        _state = null;
        _stateFrame = null;
        _screenState?.Dispose();
        _screenState = null;
        _screenFrame = null;
        _value.Dispose();
    }
}
