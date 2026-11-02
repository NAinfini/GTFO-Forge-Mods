using ForgeUI;

var failures = 0;
var checks = 0;
void Check(bool condition, string name)
{
    checks++;
    if (condition) return;
    failures++;
    Console.WriteLine("FAIL " + name);
}

// Layout order is by ID, not by registration order, so every machine shows the same stack.
var registry = new Registry<string>();
Check(registry.Add("Zeta.Mod.B", "z") == null, "add first");
Check(registry.Add("Alpha.Mod.A", "a") == null, "add second");
Check(registry.Ordered.SequenceEqual(new[] { "a", "z" }), "ordered by ID");
Check(registry.IndexOf("Zeta.Mod.B") == 1, "index follows order");
Check(registry.Add("Alpha.Mod.A", "again") != null, "duplicate refused");
Check(registry.Add("", "x") != null, "empty refused");
Check(registry.Add("has space", "x") != null, "space refused");
Check(registry.Remove("Alpha.Mod.A", "a") && registry.Ordered.SequenceEqual(new[] { "z" }), "remove");
Check(!registry.Remove("Alpha.Mod.A", "a"), "remove twice");

// Equal values are not the same registration. An obsolete handle must never remove its replacement.
var handles = new Registry<string>();
var original = new string('x', 1);
var replacement = new string('x', 1);
Check(handles.Add("Mod.Button", original) == null, "register original handle");
Check(!handles.Remove("Mod.Button", replacement), "equal but different handle cannot remove current");
Check(handles.Remove("Mod.Button", original), "current handle removes itself");
Check(handles.Add("Mod.Button", replacement) == null, "reuse ID for replacement handle");
var snapshot = handles.Ordered;
Check(!handles.Remove("Mod.Button", original), "obsolete handle cannot remove replacement");
Check(ReferenceEquals(handles.Ordered, snapshot) && ReferenceEquals(handles.Ordered.Single(), replacement),
    "rejected removal leaves replacement and snapshot intact");
Check(handles.Remove("Mod.Button", replacement), "replacement handle removes itself");
Check(!handles.Remove("Mod.Button", replacement) && handles.Ordered.Count == 0, "repeated removal is harmless");

Check(LobbyLayout.Offset(0, 0, 0) == (0f, -LobbyLayout.Step), "first button one step down");
Check(LobbyLayout.Offset(2, 5, 10) == (5f, -3 * LobbyLayout.Step + 10), "offset applied");

// Lobby strip cells fill the APPAREL button's width exactly: first cell on its left edge, last on its right edge.
foreach (var cells in new[] { 1, 4, 8 })
{
    var cellWidth = LobbyStripLayout.CellWidth(460f, cells);
    var first = LobbyStripLayout.CellCentre(-230f, cellWidth, 0) - cellWidth / 2f;
    var last = LobbyStripLayout.CellCentre(-230f, cellWidth, cells - 1) + cellWidth / 2f;
    Check(Math.Abs(first + 230f) < 1e-3f && Math.Abs(last - 230f) < 1e-3f, "lobby strip of " + cells + " cells spans the button");
}

// Menu bar: after the game's left-hand buttons, each one step on (its width or the bar spacing, whichever is larger); a button
// that would reach the right-hand group is left out, and so is every button after it, so the order never changes.
var placed = MenuBarLayout.Place(560f, 280f, new[] { 200f, 400f, 100f }, 2000f);
Check(placed[0] == 560f && placed[1] == 840f && placed[2] == 1240f, "menu bar steps by spacing or width");
placed = MenuBarLayout.Place(560f, 280f, new[] { 200f, 900f, 100f }, 1500f);
Check(placed[0] == 560f && placed[1] == null && placed[2] == null, "menu bar overflow hides the rest");
Check(MenuBarLayout.Place(0f, 280f, Array.Empty<float>(), 100f).Length == 0, "menu bar with nothing registered");

// Picker fit: shrunk to the visible span and centred in it, never enlarged, never below the readable minimum.
var (scale, origin) = PickerFit.Fit(-1300f, 800f, -1000f, 1000f);
Check(Math.Abs(scale - 2000f / 2100f) < 1e-4f && Math.Abs(origin - scale * 250f) < 1e-3f, "picker shrinks and centres");
(scale, origin) = PickerFit.Fit(-400f, 400f, -1000f, 1000f);
Check(scale == 1f && origin == 0f, "picker never enlarged");
(scale, _) = PickerFit.Fit(-2000f, 2000f, -100f, 100f);
Check(scale == PickerFit.MinScale, "picker keeps a readable size");

// Icon cell: tall, wide and square pictures are scaled to the largest size inside the cell with their ratio kept.
foreach (var (name, w, h) in new[] { ("tall figure", 1f, 3f), ("wide weapon", 4f, 1f), ("square", 2f, 2f), ("small", 0.1f, 0.2f) })
{
    var (shownW, shownH) = IconFit.Fit(40f, 43f, w, h);
    Check(shownW <= 40f + 1e-3f && shownH <= 43f + 1e-3f, name + " stays inside the cell");
    Check(Math.Abs(shownW / shownH - w / h) < 1e-4f, name + " keeps its ratio");
    Check(Math.Abs(shownW - 40f) < 1e-3f || Math.Abs(shownH - 43f) < 1e-3f, name + " fills the cell on one axis");
}
var (tallW, tallH) = IconFit.Fit(40f, 43f, 1f, 3f);
Check(Math.Abs(tallH - 43f) < 1e-3f && tallW < 40f, "tall figure is whole, head to foot");
Check(IconFit.Fit(40f, 43f, 0f, 3f) == (0f, 0f) && IconFit.Fit(0f, 43f, 1f, 3f) == (0f, 0f), "empty sizes show nothing");

InputChecks.Run(Check);
UiChecks.Run(Check);
MenuScreenChecks.Run(Check);
AuditChecks.Run(Check);
PickerIconChecks.Run(Check);
TemplateChecks.Run(Check);
LayoutChecks.Run(Check);
ScreenChecks.Run(Check);
TextChecks.Run(Check);
CapabilityChecks.Run(Check);
IconChecks.Run(Check);
ReadoutChecks.Run(Check);
CockpitChecks.Run(Check);
PowerOnChecks.Run(Check);
EnemyBarChecks.Run(Check);
LocationChecks.Run(Check);
CountdownChecks.Run(Check);
HeldMagazineChecks.Run(Check);
BootLayoutChecks.Run(Check);
ChatChecks.Run(Check);
KeyBindChecks.Run(Check);
GearNameChecks.Run(Check);
GearIconChecks.Run(Check);
CompassChecks.Run(Check);
WaypointChecks.Run(Check);
HitFeedbackChecks.Run(Check);
ConfirmChecks.Run(Check);
PatchTargetChecks.Run(Check);
ScreenEffectChecks.Run(Check);
SliderChecks.Run(Check);
TimelineChecks.Run(Check);

Console.WriteLine(failures == 0 ? "ForgeUI core: " + checks + " checks passed, 0 failed" : failures + " check(s) failed");
return failures == 0 ? 0 : 1;
