using System;
using System.Collections.Generic;

namespace ForgeCore;

/// <summary>Work that makes something ready before its first use (a shader drawn once off screen, a large texture made) and has to wait for the
/// game's picture: it runs once the elevator has stopped or the level runs, one step on each frame, in the order the steps were added. Every
/// package that warms something at the landing adds its steps here instead of running them in its own frame callback, so the warm-ups of
/// ForgeUI, ForgeVisual and a mod's own never land on the same frame. Main thread only.</summary>
public static class LandingWork
{
    private static readonly Queue<(string Owner, string Name, Action Run)> Steps = new();

    internal static void Start() => Frame.OnUpdate(Plugin.Id, Tick);

    /// <summary>Queues <paramref name="step"/>; <paramref name="owner"/> is the package's ID and <paramref name="name"/> names the step in the error report. A step that throws is
    /// reported once under ForgeCore and the steps after it still run.</summary>
    public static void Add(string owner, string name, Action step) => Steps.Enqueue((owner, name, step));

    private static void Tick()
    {
        if (Steps.Count == 0) return;
        var state = GameStateManager.CurrentStateName;
        if (state != eGameStateName.StopElevatorRide && state != eGameStateName.InLevel) return;
        var (owner, name, run) = Steps.Dequeue();
        try { run(); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("core-landing-step-failed", "The landing step " + owner + "/" + name + " failed: " + error); }
    }
}
