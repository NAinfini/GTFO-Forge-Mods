using ForgeOrdnanceFrames;

internal static class EffectsChecks
{
    internal static void Run(Action<bool, string> check)
    {
        check(FrameEffectsPlan.ArrivalThrust(0.05f) == 0f && FrameEffectsPlan.ArrivalThrust(1f) == 1f
            && FrameEffectsPlan.ArrivalThrust(0.6f) > FrameEffectsPlan.ArrivalThrust(0.4f), "arrival thrusters are dark at first and burn hardest at touchdown");
        check(FrameEffectsPlan.JetSpeed(1f) > FrameEffectsPlan.JetSpeed(0.2f) && FrameEffectsPlan.JetSize(1f) > FrameEffectsPlan.JetSize(0.2f)
            && FrameEffectsPlan.JetLife(1f) > FrameEffectsPlan.JetLife(0.2f) && FrameEffectsPlan.KickFrom > 0.8f && FrameEffectsPlan.KickFrom < 1f, "the thruster flame grows longer, wider and faster with strength, and the touchdown blast comes in the last moment");

        var life = FrameRules.WreckSeconds;
        check(life == 12f, "a wreck burns for twelve seconds");
        check(FrameEffectsPlan.WreckFire(0f, life) == 1f && FrameEffectsPlan.WreckFire(life - 6f, life) == 1f, "the wreck burns at full until its last three seconds");
        check(Math.Abs(FrameEffectsPlan.WreckFire(life - 1.5f, life) - 0.5f) < 1e-5f && FrameEffectsPlan.WreckFire(life, life) == 0f, "the fire dies down to nothing at removal");
        check(FrameEffectsPlan.WreckSink(life - 3f, life) == 0f && Math.Abs(FrameEffectsPlan.WreckSink(life - 1f, life) - 0.5f) < 1e-5f && FrameEffectsPlan.WreckSink(life, life) == 1f,
            "the wreck stands until its last two seconds, then sinks out of sight at removal");
        check(FrameEffectsPlan.WreckSmoking(0f, life) && FrameEffectsPlan.WreckSmoking(life - 2f, life) && !FrameEffectsPlan.WreckSmoking(life - 1f, life), "smoke stops a little before removal");

        check(FrameEffectsPlan.HeatLevel(0.5f, false) == 0f && Math.Abs(FrameEffectsPlan.HeatLevel(0.8f, false) - 0.5f) < 1e-5f && FrameEffectsPlan.HeatLevel(0.1f, true) == 1f,
            "haze starts at 60 percent heat and an overheated suit always shows it");
        check(FrameEffectsPlan.HeatInterval(1f) < FrameEffectsPlan.HeatInterval(0f), "haze comes quicker the hotter the suit");
        check(FrameEffectsPlan.ExplosionFlash(0f) == 1f && Math.Abs(FrameEffectsPlan.ExplosionFlash(FrameEffectsPlan.ExplosionLightSeconds / 2f) - 0.5f) < 1e-5f
            && FrameEffectsPlan.ExplosionFlash(FrameEffectsPlan.ExplosionLightSeconds) == 0f && FrameEffectsPlan.ExplosionFlash(9f) == 0f,
            "the destruction flash is full at the blast and fades straight out over its light time");
        check(Math.Abs(FrameEffectsPlan.RocketTailFlamesPerSecond - 1f / 0.045f) < 1e-3f && FrameEffectsPlan.RocketTailSmokePerSecond * 2f == FrameEffectsPlan.RocketTailFlamesPerSecond,
            "a rocket's tail leaves a flame every 45 ms and smoke with every second one");

        // Light strips: one colour per state.
        var spec = FrameSpecParser.Parse(System.Text.Json.JsonDocument.Parse("{\"Model\":\"Frames:m\",\"Armor\":100,\"Weapon\":{\"Gear\":1,\"Magazine\":1,\"Reserve\":0},"
            + "\"Light\":{\"Glow\":\"#FFC23A\"}}").RootElement);
        check(spec.Light.GlowR == 1f && Math.Abs(spec.Light.GlowG - 0xC2 / 255f) < 1e-6f && Math.Abs(spec.Light.GlowB - 0x3A / 255f) < 1e-6f, "a model's strip colour comes from its Light.Glow");
        var r = new FrameRecord(1, 1, spec);
        void Look(FrameState state, float armor, bool overheated, GlowLook expected, string name)
        {
            r.State = state;
            r.Armor = armor;
            check(FrameGlow.Look(r, overheated) == expected, "strips " + name + ": " + expected);
        }
        Look(FrameState.Arriving, 100, false, GlowLook.Standby, "falling in");
        Look(FrameState.Idle, 25, false, GlowLook.StandbyDamaged, "standing empty at a quarter armour");
        Look(FrameState.Piloted, 26, false, GlowLook.Normal, "worn above a quarter armour");
        Look(FrameState.Piloted, 25, false, GlowLook.Damaged, "worn at a quarter armour");
        Look(FrameState.Piloted, 10, true, GlowLook.Overheat, "worn and overheated, even when damaged");
        Look(FrameState.Entering, 10, true, GlowLook.Opening, "opening for a pilot");
        Look(FrameState.Exiting, 100, false, GlowLook.Opening, "opening for a pilot leaving");
        Look(FrameState.Destroyed, 0, false, GlowLook.Off, "wrecked");
        var l = spec.Light;
        (float R, float G, float B) C(GlowLook look, GlowLook from, float age, float now = 0f) => FrameGlow.Colour(l, look, from, age, now);
        float Peak((float R, float G, float B) c) => Math.Max(c.R, Math.Max(c.G, c.B));
        var normal = C(GlowLook.Normal, GlowLook.Normal, 5f);
        check(normal.R == FrameGlow.Strength && Math.Abs(normal.G / normal.R - 0xC2 / 255f) < 1e-5f, "worn, the strips glow in the model's hue at full strength");
        var breaths = Enumerable.Range(0, 40).Select(i => Peak(C(GlowLook.Standby, GlowLook.Standby, 0f, i * FrameGlow.BreathSeconds / 40f)) / FrameGlow.Strength).ToArray();
        check(breaths.Min() > 0.11f && breaths.Max() < 0.23f && breaths.Max() - breaths.Min() > 0.08f, "an empty suit breathes between about 12 and 22 percent");
        check(FrameGlow.Moving(GlowLook.Standby, GlowLook.Standby, 100f) && FrameGlow.Moving(GlowLook.Overheat, GlowLook.Normal, 100f)
            && !FrameGlow.Moving(GlowLook.Normal, GlowLook.Normal, 0f) && !FrameGlow.Moving(GlowLook.Opening, GlowLook.Standby, 0f), "only breathing, pulsing and transitions write every frame");
        var heat = new[] { 0f, 0.125f, 0.25f, 0.375f }.Select(a => Peak(C(GlowLook.Overheat, GlowLook.Normal, a))).ToArray();
        check(heat[0] == FrameGlow.Strength && heat[2] < 0.35f * FrameGlow.Strength && Math.Abs(heat[1] - heat[3]) < 1e-4f, "overheat pulses twice a second");
        var hot = C(GlowLook.Overheat, GlowLook.Normal, 0f);
        check(hot.G / hot.R < 0.35f && Math.Abs(hot.G / hot.R - normal.G / normal.R) > 0.4f, "overheat is a red-orange far from the gold model colour");
        check(Peak(C(GlowLook.Normal, GlowLook.Opening, 0.1f)) < 0.1f && Peak(C(GlowLook.Normal, GlowLook.Opening, 0.29f)) == FrameGlow.Strength
            && Peak(C(GlowLook.Normal, GlowLook.Opening, 0.4f)) < 0.1f && Peak(C(GlowLook.Normal, GlowLook.Opening, 0.5f)) == FrameGlow.Strength
            && Peak(C(GlowLook.Normal, GlowLook.Opening, 0.75f)) < Peak(normal) && !FrameGlow.Moving(GlowLook.Normal, GlowLook.Opening, FrameGlow.BootSeconds),
            "power-on: dark, two flashes, then up to full");
        var flash = C(GlowLook.Damaged, GlowLook.Normal, 0.05f);
        var red = C(GlowLook.Damaged, GlowLook.Normal, 1f);
        check(flash.G > 0.3f && red.G < 0.1f && !FrameGlow.Moving(GlowLook.Damaged, GlowLook.Normal, FrameGlow.DamageFlashSeconds), "falling to red flashes once, then stays red");
        var flicker = Enumerable.Range(0, 12).Select(i => Peak(C(GlowLook.Off, GlowLook.Normal, i * 0.04f))).ToArray();
        check(flicker.Max() > 0.6f && flicker.Min() < 0.1f && C(GlowLook.Off, GlowLook.Normal, 0.5f) == (0f, 0f, 0f) && C(GlowLook.Off, GlowLook.Off, 0f) == (0f, 0f, 0f),
            "a wreck's strips flicker for half a second, then go dark");
        var open = C(GlowLook.Opening, GlowLook.Standby, 0f);
        check(open.B > 0.3f * open.R && open.B < 0.8f * open.R, "opening is a highlight of the model's colour, not white");
    }
}
