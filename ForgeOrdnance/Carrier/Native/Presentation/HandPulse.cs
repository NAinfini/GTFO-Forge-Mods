using ForgeVisual;
using UnityEngine;

namespace ForgeOrdnance;

/// <summary>The brief light pulse at the ball when a call-in code is completed (a ForgeVisual <see cref="GlowSpot"/> on the local camera,
/// 0.3 s). Local player only, so it is never in the way and nothing is sent to other players.</summary>
internal static class HandPulse
{
    private const float Seconds = 0.3f, StartSize = 0.04f, EndSize = 0.3f, Alpha = 0.85f;
    /// <summary>Where the held ball sits: low and to the right of the view centre, a little ahead.</summary>
    private static readonly Vector3 Hand = new(0.15f, -0.14f, 0.5f);
    private static GlowSpot? _spot;
    private static Transform? _on;

    /// <summary>The local camera, or null outside a level.</summary>
    private static Transform? Eye()
    {
        var agent = ForgeCore.Players.Local;
        var own = agent != null && !agent.WasCollected ? agent.FPSCamera : null;
        if (own != null) return own.transform;
        var cam = Camera.main;
        return cam != null ? cam.transform : null;
    }

    /// <summary>Pulses in the call's beam colour (0xRRGGBB), brightened a little.</summary>
    internal static void Start(uint beamRgb)
    {
        var eye = Eye();
        if (eye == null) return;
        if (_spot == null || !_spot.Alive || _on != eye)
        {
            _spot?.Destroy();
            _spot = GlowSpot.Create(eye, Hand);
            _on = eye;
        }
        if (OrdnanceFxSlots.HandPulse is { } pulse)
        {
            var forward = eye.forward;
            Fx.Play(pulse, eye.position + forward * 0.6f, forward);
        }
        _spot?.Pulse(Color.Lerp(ForgeCore.Colours.FromRgb(beamRgb), Color.white, 0.35f), Seconds, StartSize, EndSize, Alpha);
    }
}
