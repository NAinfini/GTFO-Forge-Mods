"""Offline source checks for the boarding and leaving paths: everything they show is created ahead of time (the prewarm, the shell built for the
nearest idle suit), so the methods that run while boarding, wearing and leaving may only switch, set and draw. Reads the C# sources; a method listed
here that creates a GameObject, Material, Mesh, texture, render texture or component, instantiates anything, loads a bundle, looks a shader up or
searches the scene is a failure."""
from pathlib import Path
import os
import re
import unittest

ROOT = Path(__file__).parents[2]
NATIVE = ROOT / "Native"
MODELS = os.environ.get("FORGE_FRAMES_MODELS")
UIKIT = Path(__file__).parents[3] / "ForgeUI" / "Native"
CORE = Path(__file__).parents[3] / "ForgeCore" / "Native"
VISUAL = Path(__file__).parents[3] / "ForgeVisual" / "Native"

# File -> the methods that run during boarding, wearing and leaving (lazy first-use fallbacks such as the HUD pools' growth live in other methods).
HOT = {
    # ForgeUI's power-on player and power-on message the suit plays, and the screen overlays under them.
    UIKIT / "PowerOn.cs": ["Fade", "PowerOn", "PowerDown", "Hide"],
    UIKIT / "Templates" / "HudBootMessage.cs": ["Play", "PowerDown", "Stop", "Tick", "Draw"],
    UIKIT / "ScreenLayer.cs": ["Fade", "Panel", "Seams", "Hide", "Park", "Changed", "Show", "SetAlpha", "Active", "Use"],
    "FrameHud.cs": ["Board", "Sync", "Update", "CheckOnScreen", "Entering", "Leaving", "Hide", "Parts", "Part", "Feed"],
    # ForgeVisual's first-person wear (the shell) and body clip (the leaving motion) the suit switches while boarding, wearing and leaving.
    VISUAL / "BodyWear.cs": ["Activate", "Deactivate", "Hold", "Follow"],
    VISUAL / "BodyClip.cs": ["Pose", "Stop", "Pause", "FirstPerson"],
    "LocalPilot.cs": ["SyncHud", "PowerOn", "BootSounds", "Undress", "Dress", "Prepare", "LateTick", "GetInside", "Leave"],
    "PilotBody.cs": ["Pose", "Stop", "FirstPerson"],
    # The view lease the pilot writes the eye to every worn frame (the eye height, near plane and look are applied by ForgeCore).
    CORE / "CameraLease.cs": ["Lift", "NearClip", "Look", "Apply", "ApplyLift", "ApplyNear"],
}
FORBIDDEN = re.compile(
    r"new\s+GameObject\b|new\s+Material\b|new\s+Mesh\b|new\s+Texture2D\b|new\s+RenderTexture\b|new\s+Sprite\b|Sprite\.Create\b|Instantiate\s*\(|AddComponent\b|CreatePrimitive\b|"
    r"Resources\.|FindObjectsOfType|FindObjectOfType|GetComponentsInChildren|GetComponentsInParent|Shader\.Find\b|AssetBundle\.|LoadAsset\b|GetTemporary\b")


def body(source, name):
    """The text of method <name> (block or expression body), or None."""
    match = re.search(r"^[ \t]*(?:internal|private|public)[^\n;=]*\b" + re.escape(name) + r"\s*(?:<[^>]*>)?\s*\([^)]*\)\s*(?:where[^\n{]*)?\s*(\{|=>)", source, re.M)
    if not match:
        return None
    if match.group(1) == "=>":
        return source[match.end():source.index(";", match.end())]
    depth, i = 1, match.end()
    while depth:
        c = source[i]
        depth += (c == "{") - (c == "}")
        i += 1
    return source[match.end():i]


def code_only(text):
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    return re.sub(r"//[^\n]*", "", text)


class BoardingChecks(unittest.TestCase):
    def test_hot_paths_create_nothing(self):
        for file, methods in HOT.items():
            source = (file if isinstance(file, Path) else NATIVE / file).read_text(encoding="utf-8")
            for name in methods:
                found = body(source, name)
                self.assertIsNotNone(found, str(file) + " has no method " + name)
                hit = FORBIDDEN.search(code_only(found))
                self.assertIsNone(hit, str(file) + "." + name + " creates or searches something while boarding or leaving: " + (hit.group(0) if hit else ""))

    def test_prewarm_builds_what_boarding_shows(self):
        prewarm = (NATIVE / "FramePrewarm.cs").read_text(encoding="utf-8")
        for needed in ("RegisterScreens", "WarmNextLayer", "WarmHud", "FrameFx.Warm"):
            self.assertIn(needed, prewarm)
        pilot = (NATIVE / "LocalPilot.cs").read_text(encoding="utf-8")
        self.assertIn("FramePrewarm.Tick", pilot)
        build = code_only(body(pilot, "BuildShellsInElevator") or "")
        self.assertIn("ElevatorRide.ElevatorRideInProgress", build, "the shells are built only while the elevator rides down")
        self.assertIn("OrdnanceApi.PickedParameters", build, "only the models the players picked in the lobby")
        self.assertIn("LocalPilot.Called", (NATIVE / "FramesPayload.cs").read_text(encoding="utf-8"), "a called-in model is built at its beacon, before it lands")
        self.assertNotIn("EnsureShell", code_only(body(pilot, "Prepare") or ""), "nothing is built while the player can act")
        hud = (NATIVE / "FrameHud.cs").read_text(encoding="utf-8")
        self.assertIn("_link.Warm()", hud, "the power-on message is drawn once in the prewarm")
        message = (UIKIT / "Templates" / "HudBootMessage.cs").read_text(encoding="utf-8")
        self.assertIn("PrepareText", code_only(body(message, "Warm") or ""), "the message's texts make their glyphs in the prewarm")

    def test_sequences_share_the_state_clock(self):
        """Boarding, leaving and the power-on run on the state's start as the suit's visual saw it: a stalled frame skips ahead."""
        pilot = (NATIVE / "LocalPilot.cs").read_text(encoding="utf-8")
        self.assertNotIn("ClockStep", pilot, "no per-frame clamp stretches a stall into slow motion")
        self.assertIn("unit.Visual?.StateStart", pilot, "the pilot's clock is the plates' clock")
        self.assertIn("now - _bootStart", pilot)
        self.assertIn("_down = elapsed", code_only(body(pilot, "Tick") or ""))

    def test_opening_moves_parts_rigidly(self):
        """The definitions only name the parts (and mark the back's seam); the game plays the model's "open" and "close" clips
        (the owner's posed paths) and works out no motion of its own; the bundle build fails on a part that scales in a clip."""
        import json
        frames = json.loads((ROOT / "Definitions" / "frames.json").read_text(encoding="utf-8"))
        for frame in frames:
            parts = frame["Payload"]["Opening"]["Parts"]
            self.assertTrue(parts, frame["Id"] + " has no opening parts")
            for part in parts:
                self.assertLessEqual(set(part), {"Node", "Seam"}, frame["Id"] + " " + part["Node"] + " carries motion for the runtime to compute")
        open_body = code_only(body((NATIVE / "FrameVisual.cs").read_text(encoding="utf-8"), "Open") or "")
        self.assertTrue(open_body, "FrameVisual has no Open")
        self.assertIn("SampleAnimation", open_body)
        for computed in ("localScale", "localRotation", "localPosition", "AngleAxis"):
            self.assertNotIn(computed, open_body)
        self.assertIn("changes its scale while opening", (ROOT / "tools" / "FramesBundleBuild.cs").read_text(encoding="utf-8"))
        self.assertNotIn("Retract", (ROOT / "Core" / "FrameSpec.cs").read_text(encoding="utf-8"))

    def test_one_visible_mesh_per_suit(self):
        """One skinned renderer per suit and no LOD copies, so no plate is drawn twice; the bundle build fails otherwise and checks scale 1."""
        build = (ROOT / "tools" / "FramesBundleBuild.cs").read_text(encoding="utf-8")
        self.assertIn("renderers.Length != 1", build)
        self.assertNotIn("LODGroup", build)
        self.assertIn("OPENING_SCALE", build)
        self.assertIn("SLOTS ", build)

    def test_built_openings_are_clear_and_single_layer(self):
        """The model builds' entry reports (local resources): every pilot-slot ray blocked shut (the slots are inside the suit),
        the parts taken from the owner's cut file, no second layer left behind under an opening part and no small piece moving
        with a part it does not touch. The rays left blocked open and the parts' paths through the body or each other are only
        reported (the owner judges the opening from its pictures), so they are not asserted here."""
        reports = sorted(Path(MODELS).glob("M*/M*_entry.json")) if MODELS else []
        if not reports:
            self.skipTest("needs local resources, not run (set FORGE_FRAMES_MODELS to the built models folder)")
        import json
        for report in reports:
            entry = json.loads(report.read_text(encoding="utf-8"))
            name = report.stem
            self.assertGreater(entry["rays"], 0, name)
            self.assertEqual(entry["raysBlockedShut"], entry["rays"], name)
            self.assertEqual(len(entry["slotRays"]), entry["rays"], name)
            self.assertEqual(entry["selection"], "owner's cut file", name)
            self.assertEqual(entry["hovering"], [], name)
            self.assertEqual(entry["secondLayersLeft"], 0, name)
            self.assertEqual(entry["motion"], "rigid paths from the cut file", name)


if __name__ == "__main__":
    unittest.main()
