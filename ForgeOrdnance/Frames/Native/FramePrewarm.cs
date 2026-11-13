namespace ForgeOrdnance.Frames;

/// <summary>Everything boarding and leaving need is created and compiled long before anyone boards, a step per frame, hidden: the
/// screen layers (the power-on panels registered first, then ForgeUI's
/// prewarm builds the suit's layers one per step, the hit glow and the visor included, each drawn once off screen so the shaders and the
/// screen grab are compiled), and the power-on message's pieces and glyphs. After it, <see cref="LocalPilot"/> builds the first-person shell (a copy
/// of the suit model) of every model, so a suit's landing and boarding build nothing.
/// Boarding and leaving then only switch things on and off. Runs while the player is not in a suit.</summary>
internal static class FramePrewarm
{
    private const float Delay = 1.5f, Spacing = 0.1f;
    private const int LayersStage = 1, Stages = 3;

    /// <summary>Every step has run for this level.</summary>
    internal static bool Done => _stage >= Stages;

    private static float _readyAt = -1f, _nextAt;
    private static int _stage;

    /// <summary>One step per call when due; <paramref name="sample"/> is any suit of the level (its HUD is what gets built).</summary>
    internal static void Tick(FrameHud hud, FrameRecord sample, float now)
    {
        if (_stage >= Stages) return;
        if (_readyAt < 0f) { _readyAt = now + Delay; return; }
        if (now < _readyAt || now < _nextAt) return;
        var next = true;
        switch (_stage)
        {
            case 0:
                hud.RegisterScreens();
                break;
            case LayersStage:
                // ForgeUI builds one layer per call; this stage repeats until every layer is ready.
                if (hud.WarmNextLayer() == null) { _stage++; return; }
                next = false;
                break;
            default:
                if (!hud.WarmHud(sample, now)) return;
                break;
        }
        _nextAt = now + Spacing;
        if (next) _stage++;
    }

    /// <summary>The level ended: the HUD is rebuilt by the game and warmed again; the screen layers and the shaders live on (ForgeUI
    /// rebuilds a layer only when the screen size changed).</summary>
    internal static void LevelEnded()
    {
        _readyAt = -1f;
        _nextAt = 0f;
        _stage = 0;
    }
}
