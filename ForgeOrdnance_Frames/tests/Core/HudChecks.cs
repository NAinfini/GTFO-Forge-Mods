using ForgeOrdnanceFrames;

internal static class HudChecks
{
    internal static void Run(Action<bool, string> check, FrameSpec assault)
    {
        var words = LangChecks.Words();
        var tank = assault with { Boost = null, Shield = new ShieldSpec(50f, 6f, 10f, 2.75f, 0.5f, false), CarryHeavy = true };
        var gun = new FrameGunReading(true, 30, 60, false);
        var hud = new FrameHudModel(words);

        // Armour: the bar reads the armour's share, planted or not; ForgeUI's segment bar eases it, numbers it and raises its alarm
        // (warn, then critical with a flash per segment lost, then failing), on the suit's thresholds.
        var record = new FrameRecord(1, 1, assault) { Armor = 60f };
        hud.Update(record, gun, 0f, false, 0f, -99f, 0f);
        check(Math.Abs(hud.ArmorFraction - 60f / assault.Armor) < 1e-4f, "the armour bar reads the armour's share");
        var planted = new FrameRecord(1, 9, assault with { Boost = null, DamageReduction = 0.3f, Siege = new SiegeSpec(1f, 1.4f, 0.5f, 1f, 1f, 1f) }) { Armor = 75f };
        hud.Update(planted, gun, 0f, false, 0f, -99f, 0f);
        var walking = hud.ArmorFraction;
        planted.Sieged = true;
        hud.Update(planted, gun, 0f, false, 0f, -99f, 0f);
        check(walking == hud.ArmorFraction && Math.Abs(walking - 75f / planted.Spec.Armor) < 1e-4f, "planted or not, the bar reads the armour alone");
        check(FrameAlerts.FailingArmor < FrameHudModel.LowArmor && FrameHudModel.LowArmor < FrameHudModel.WarnArmor && FrameHudModel.ArmorEase > 0f,
            "the armour bar warns, turns critical and then fails, in that order");

        // The barrier line: its charge, broken with the wait then the refill, standby for a planted-only barrier until planted.
        var tankRecord = new FrameRecord(1, 4, tank) { Shield = 25f, ShieldUp = true, State = FrameState.Piloted, Pilot = 1 };
        hud.Update(tankRecord, gun, 0f, false, 0f, -99f, 0f);
        check(hud.Barrier == BarrierState.Up && Math.Abs(hud.BarrierFraction - 0.5f) < 1e-4f && hud.BarrierRecovery < 0f,
            "a standing barrier shows its charge");
        var broken = new FrameRecord(1, 12, tank) { Shield = 0f, ShieldUp = false, ShieldLastHit = 7f };
        hud.Update(broken, gun, 0f, false, 10f, -99f, 0f);
        var waiting = hud.Barrier == BarrierState.Broken && Math.Abs(hud.BarrierRecovery - 0.25f) < 1e-3f;
        broken.Shield = 12.5f;
        hud.Update(broken, gun, 0f, false, 14f, -99f, 0f);
        check(waiting && Math.Abs(hud.BarrierRecovery - 0.75f) < 1e-3f, "a broken barrier fills its way back: the wait, then the refill");
        var gunner = new FrameRecord(1, 10, assault with { Boost = null, Siege = new SiegeSpec(1f, 1.4f, 0.2f, 1.35f, 0.65f, 1f), Shield = new ShieldSpec(100f, 5f, 20f, 2.2f, 0.5f, true) })
            { State = FrameState.Piloted, Pilot = 1, Shield = 100f, ShieldUp = true };
        hud.Update(gunner, gun, 0f, false, 0f, -99f, 0f);
        var standby = hud.Barrier == BarrierState.Standby;
        gunner.Sieged = true;
        hud.Update(gunner, gun, 0f, false, 0f, -99f, 0f);
        check(standby && hud.Barrier == BarrierState.Up, "a planted-only barrier stands by and stands once planted");
        // Siege on and off: the barrier layer and its number are drawn only while planted, reading the barrier's points.
        var siegeShown = hud.BarrierShown && hud.BarrierPoints == 100 && Math.Abs(hud.BarrierFraction - 1f) < 1e-4f;
        gunner.Shield = 37.2f;
        hud.Update(gunner, gun, 0f, false, 1f, -99f, 0f);
        var struck = hud.BarrierShown && hud.BarrierPoints == 38 && Math.Abs(hud.BarrierFraction - 0.372f) < 1e-4f;
        gunner.Sieged = false;
        hud.Update(gunner, gun, 0f, false, 2f, -99f, 0f);
        var packed = !hud.BarrierShown && hud.Barrier == BarrierState.Standby;
        gunner.Sieged = true; gunner.Shield = 6f; gunner.ShieldUp = false; gunner.ShieldLastHit = 2f;
        hud.Update(gunner, gun, 0f, false, 3f, -99f, 0f);
        var brokenPlanted = hud.BarrierShown && hud.Barrier == BarrierState.Broken && hud.BarrierFraction == 0f && hud.BarrierPoints == 0 && hud.BarrierRecovery > 0f;
        gunner.Sieged = false;
        hud.Update(gunner, gun, 0f, false, 3f, -99f, 0f);
        check(siegeShown && struck && packed && brokenPlanted && !hud.BarrierShown,
            "siege on shows the barrier's points (100, 38 after hits, 0 broken while it refills); siege off hides the layer and its number");
        hud.Update(tankRecord, gun, 0f, false, 0f, -99f, 0f);
        check(hud.BarrierShown && hud.BarrierPoints == 25, "an always-standing barrier is always drawn with its points");
        gunner.ShieldUp = true; gunner.Shield = 100f; gunner.Sieged = true;
        hud.Update(new FrameRecord(1, 5, assault), gun, 0f, false, 0f, -99f, 0f);
        check(hud.Barrier == BarrierState.None, "a suit without a barrier has no barrier line");

        // Specials: the charge cools down; the siege shows its deploy and, planted, the gun's boosts; the scan has no cooldown and lights as it runs.
        var charge = new FrameRecord(1, 6, assault) { BoostReadyAt = 30f };
        hud.Update(charge, gun, 0f, false, 20f, -99f, 0f);
        check(hud.Special == FrameSpecialKind.Charge && hud.SpecialName == "CHARGE" && Math.Abs(hud.SpecialRemaining - 10f) < 1e-4f && hud.SpecialCooldown == assault.Boost!.Cooldown && !hud.SpecialActive,
            "the charge counts down its cooldown");
        var siegeSpec = assault with { Boost = null, Siege = new SiegeSpec(1.2f, 1.2f, 0.1f, 2f, 0.5f, 1f) };
        var siege = new FrameRecord(1, 11, siegeSpec) { SiegeDeployEndsAt = 10.6f };
        hud.Update(siege, gun, 0f, false, 10f, -99f, 0f);
        var deploying = Math.Abs(hud.SpecialProgress - 0.5f) < 1e-3f && !hud.SpecialActive && hud.SpecialRemaining == 0f;
        siege.SiegeDeployEndsAt = 0f; siege.Sieged = true;
        hud.Update(siege, new FrameGunReading(true, 0, 60, true, 0.4f), 0f, false, 11f, -99f, 0f);
        check(deploying && hud.SpecialProgress < 0f && hud.SpecialActive && Math.Abs(hud.ReloadProgress - 0.4f) < 1e-4f,
            "planting shows its deploy; planted, the slot is lit; the reload shows its progress");
        var scout = assault with { Boost = null, Scan = new ScanSpec(10f, 5f) };
        hud.Update(new FrameRecord(1, 7, scout), gun, 0f, false, 100f, 99.8f, 0f);
        var lit = hud.SpecialActive && hud.SpecialRemaining == 0f && hud.SpecialName == "SCAN";
        hud.Update(new FrameRecord(1, 7, scout), gun, 0f, false, 101f, 99.8f, 0f);
        check(lit && !hud.SpecialActive && hud.SpecialRemaining == 0f, "the scan shows no cooldown and lights its slot only as it runs");
        var rockets = assault with { Rockets = new RocketsSpec(4, 20f, 4f, 1.5f, 12f, 60f, 40f, new RocketLauncherSpec("pod", "round", "chest", 0.3f, 1.9f, 0f, 0f, 0f, 0f)) };
        hud.Update(new FrameRecord(1, 8, rockets), gun, 0.5f, true, 50f, -99f, 56f);
        check(hud.HasRockets && Math.Abs(hud.RocketRemaining - 6f) < 1e-4f && hud.RocketCooldown == 12f && hud.Heat == 0.5f && hud.Overheated,
            "rockets count down; heat and overheat are read as they are");
        var spent = new FrameRecord(1, 13, assault) { Magazine = 0, Reserve = 0 };
        hud.Update(spent, default, 0f, false, 0f, -99f, 0f);
        check(hud.Clip == 0 && hud.Reserve == 0 && hud.ReloadProgress < 0f, "the suit gun returned: its stock reads from the record");

        // Banners only for the suit failing: once when armour drops under a tenth, never on the first look.
        var alerts = new FrameAlerts();
        gunner.Armor = gunner.Spec.Armor;
        var first = alerts.Next(gunner);
        gunner.Armor = gunner.Spec.Armor * 0.2f;
        var lower = alerts.Next(gunner);
        gunner.Armor = gunner.Spec.Armor * 0.05f;
        var failing = alerts.Next(gunner);
        var again = alerts.Next(gunner);
        alerts.Reset();
        var boarded = alerts.Next(gunner);
        var keys = new FrameWords(key => key);
        check(first == FrameAlert.None && lower == FrameAlert.None && failing == FrameAlert.Failing && again == FrameAlert.None && boarded == FrameAlert.None
              && FrameAlerts.Text(FrameAlert.Failing, keys) == "banner.failing" && FrameAlerts.Tone(FrameAlert.Failing) == FrameAlertTone.Alert,
            "the only banner of the worn suit is its failing armour, once, red; boarding a wreck announces nothing");
        // Boarding (2.85 s, no pilot motion): the opening clip is done by the black (1.4 times its own 2.63 s pace), the close clip plays from
        // the black at twice its own 1.9 s pace (it must fit the 0.95 s left), and is latched by the end, when the pilot moves.
        const float opening = 2.63f, closing = 1.9f;
        var enter = (float t) => FrameSequence.EnterPlates(t, opening, closing);
        check(enter(0f) == new PlatePose(false, 0f) && Math.Abs(enter(FrameSequence.OpenBy * 0.5f).Time - opening * 0.5f) < 1e-4f && Math.Abs(enter(FrameSequence.OpenBy - 1e-3f).Time - opening) < 0.01f
            && !enter(FrameSequence.CloseStart - 0.01f).Closing && enter(FrameSequence.CloseStart) == new PlatePose(true, 0f)
            && Math.Abs(enter(FrameSequence.CloseStart + 0.5f).Time - 0.5f * closing / (FrameSequence.EnterSeconds - FrameSequence.CloseStart)) < 1e-4f && enter(FrameSequence.EnterSeconds) == new PlatePose(true, closing),
            "boarding opens the shell by the black at 1.9 s (about 1.4 times the clip's pace), closes it from there at twice the clip's own pace and is latched by 2.85 s");
        check(Math.Abs(FrameSequence.EnterShut(closing) - FrameSequence.EnterSeconds) < 1e-4f && Math.Abs(FrameSequence.EnterShut(0.5f) - (FrameSequence.CloseStart + 0.5f)) < 1e-4f,
            "a close that does not fit ends with the boarding, a short one at its own pace");
        check(FrameSequence.PilotShownEntering(FrameSequence.Black - 0.01f) && !FrameSequence.PilotShownEntering(FrameSequence.Black)
            && FrameSequence.EnterFade(FrameSequence.Black - FrameSequence.FadeSeconds - 0.01f) == 0f && FrameSequence.EnterFade(FrameSequence.Black) > 0.999f
            && FrameSequence.EnterFade(FrameSequence.Black - FrameSequence.FadeSeconds / 2f) is > 0.4f and < 0.6f,
            "the pilot stands where they boarded from until the black and is gone from then; their own screen fades to black over the last 0.3 s before it");
        check(FrameSequence.Play(0.25f, 0f, 0.5f, 1f) == 0.5f && FrameSequence.Play(0.5f, 0f, 0.5f, 1f) == 1f && FrameSequence.Play(0.25f, 0f, 2f, 1f) == 0.25f,
            "a clip longer than its window plays faster, a shorter one at its own pace");
        check(FrameSequence.OpenBy == FrameSequence.Black && FrameSequence.CloseStart == FrameSequence.Black && Math.Abs(FrameSequence.EnterSeconds - 2.85f) < 1e-4f
              && Math.Abs(closing / (FrameSequence.EnterSeconds - FrameSequence.CloseStart) - 2f) < 0.01f,
            "the shell stands open at the black and closes from there at twice its clip's pace; the power-on runs from the black and the pilot moves at 2.85 s");
        // Leaving: the plates open under the power-down, the leaving motion starts when every block is black, the black clears once the
        // head is out, the leaving ends on the landing; the empty suit closes afterwards on its own.
        var exit = (float t) => FrameSequence.ExitPlates(t, opening);
        check(exit(0f) == new PlatePose(false, 0f) && exit(FrameSequence.ExitClipStart) == new PlatePose(false, opening) && exit(FrameSequence.ExitSeconds) == new PlatePose(false, opening),
            "leaving opens the back under the power-down and holds it open to the landing");
        check(FrameSequence.ClosePlates(0f, opening, closing) == new PlatePose(false, opening) && FrameSequence.ClosePlates(FrameSequence.CloseAfterLand, opening, closing) == new PlatePose(true, 0f)
              && FrameSequence.ClosePlates(FrameSequence.CloseAfterLand + closing + 0.01f, opening, closing) == null,
            "after the landing the empty suit closes on its own at the clip's pace, holding nobody");
        check(FrameSequence.ExitClipStart == FrameBootScreen.DownDark && FrameBootScreen.DownClearStart == FrameSequence.ExitClipStart + PilotMotion.HeadOut
              && Math.Abs(FrameSequence.ExitSeconds - (FrameSequence.ExitClipStart + PilotMotion.Land)) < 1e-5f && FrameSequence.ExitSeconds < 1.8f,
            "the pilot pulls out once every block is black, the black clears once the head is out of the shell, and the pilot moves on the landing");
        check(!FrameSequence.PilotShownExiting(FrameSequence.ExitClipStart - 0.01f) && FrameSequence.PilotShownExiting(FrameSequence.ExitClipStart),
            "the pilot's body appears as the leaving motion starts");        check(FrameRules.BoardDistance > FrameRules.BoardSpot && FrameRules.BoardDistance < 1f
            && FrameRules.TetherReach >= 1.5f * FrameSequence.ExitClearance + 0.6f + 0.5f,
            "a player boards only from close behind the suit, where the walk-round arrow points; the tether outreaches every exit spot");
        FrameSequence.Ahead(0f, 0f, 90f, 1f, out var ax, out var az);
        check(Math.Abs(ax - 1f) < 1e-4f && Math.Abs(az) < 1e-4f, "ahead follows the yaw (90 degrees faces +x)");

        BootChecks.Run(check);
        SceneChecks.Run(check);
    }
}
