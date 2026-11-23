using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using BepInEx;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using ForgeCore;

namespace ForgeMap.Native;

/// <summary>The Rundown menu of one content generation (contract I-RELOAD tier C): the packs that hold menu positions, the native
/// rows they publish, the menu values written for them and the names shown. <see cref="Current"/> is the generation's menu the menu
/// callbacks answer through; a new generation installs its menu in place of the previous one (the previous one's writes go back to
/// their baselines first). Reflection is confined to loading and menu callbacks (including retries while a progression receipt is
/// pending); no execution-plan path passes through it.</summary>
internal sealed class RundownMenu
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;
    // Preserve every discovered wrapper for host identity preflight even if menu usability later rejects it.
    internal static IReadOnlyList<RundownPack> CapturedPacks { get; private set; } = Array.Empty<RundownPack>();
    /// <summary>The menu the callbacks answer through: the committed generation's, or null before the first generation.</summary>
    internal static RundownMenu? Current { get; private set; }
    private static readonly Dictionary<string, Type> Tables = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, object?> PlayerBaseline = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, object> PlayerWritten = new(StringComparer.Ordinal);
    // Key by native identity: IL2CPP may supply a different managed wrapper to PlaceRundown.
    // Retain the pending page's GC handle until its receipt is consumed, not an old progression snapshot.
    private static readonly Dictionary<IntPtr, (object Page, uint RundownId, string Reason, string MissingExpedition)> DeferredProgression = new();
    private static int _capacity = -1;
    private static Action<string> _report = null!;
    private static Action<RundownDiagnostic>? _refusePlans;
    private static bool _menuAvailable = true, _namesPerLanguage;
    private const string AdmittedCode = "rundown.admitted";

    private IReadOnlyList<RundownPack> _loaded = Array.Empty<RundownPack>();
    private IReadOnlyDictionary<uint, RundownName> _achievementRundowns = new Dictionary<uint, RundownName>();
    // The menu values this menu wrote while installed, each with the value it found (contract I-RELOAD baseline patch).
    private readonly List<ContentLease> _patches = new();
    private bool _installed;
    /// <summary>The captured packs that take menu positions (one per slot, or the single-Rundown package), whose native types resolved.</summary>
    internal IReadOnlyList<RundownPack> Installed { get; }
    private static IReadOnlyList<RundownPack> Loaded => Current?._loaded ?? Array.Empty<RundownPack>();

    /// <summary>The process's menu settings, at Load. The menu callbacks are the declared hook classes of <c>RundownMenuHooks.cs</c>,
    /// installed by Runtime; each one answers only once a pack was admitted, except the progression pair, which also protects pages
    /// with no admitted Forge pack. <paramref name="namesPerLanguage"/>: whether the level checksum hook (<see cref="LevelNameChecksum"/>)
    /// is on: only then a level name shows each machine's own language; without it the shipped primary-language name is left as it is.</summary>
    internal static void Start(Action<string> report, IReadOnlyList<RundownPack>? captured = null,
        Action<RundownDiagnostic>? refusePlans = null, bool menuAvailable = true, bool namesPerLanguage = false)
    {
        _report = report;
        _refusePlans = refusePlans;
        _menuAvailable = menuAvailable;
        _namesPerLanguage = namesPerLanguage;
        Current = null;
        DeferredProgression.Clear();
        CapturedPacks = captured ?? ContentPackages.ReadRundowns(Paths.BepInExRootPath, ContentPackages.EngineReader, Report);
        ForgeCore.GameEvents.LanguageChanged -= ShowCurrentNames;
        ForgeCore.GameEvents.LanguageChanged += ShowCurrentNames;
    }

    /// <summary>The menu of one discovery: the captured packs that take menu positions and whose native block types resolve.</summary>
    internal RundownMenu(IReadOnlyList<RundownPack> captured)
    {
        if (!_menuAvailable)
        {
            // A Rundown shown without its title, slot or player values would be a wrong menu, so with any of the
            // menu callbacks refused no pack is installed, and each one says why by name.
            Installed = Array.Empty<RundownPack>();
            foreach (var pack in captured)
                Report(new(RundownPacks.RejectedCode, pack.Package, pack.Rundown, "",
                    "native-hook-unavailable: a main-menu hook was refused, so Rundown packages are not installed"));
            return;
        }
        // Generic native block types and their IL2CPP collection types are selected from the verified package
        // schema. Resolve them once; using managed JSON deserialization would create unusable managed objects.
        var usable = new List<RundownPack>();
        foreach (var pack in RundownPacks.SelectSlots(captured, Report))
        {
            try
            {
                foreach (string type in pack.Blocks.Select(block => block.Type).Distinct())
                    Tables[type] = NativeType("GameData." + type + "DataBlock");
                usable.Add(pack);
            }
            catch (Exception error) { Report(new(RundownPacks.RejectedCode, pack.Package, pack.Rundown, "", error.GetBaseException().Message)); }
        }
        Installed = usable;
        if (usable.Count != 0) foreach (string type in new[] { "GameSetup", "Player" }) Tables[type] = NativeType("GameData." + type + "DataBlock");
    }

    private static Type NativeType(string name) => typeof(GameData.RundownDataBlock).Assembly.GetType(name, false)
        ?? throw new InvalidOperationException("Rundown native type unavailable: " + name);
    // The menu callbacks run every frame while a Rundown page is open, so each member lookup is resolved once per
    // (type, name): the native types are fixed for the life of the process. Values are still read and written on
    // every call; only the reflection metadata is retained.
    private sealed record Accessor(PropertyInfo? Property, FieldInfo? Field);
    private static readonly ConcurrentDictionary<(Type, string), Accessor> Accessors = new();
    private static readonly ConcurrentDictionary<(Type, string, int), MethodInfo[]> MethodCandidates = new();
    private static readonly ConcurrentDictionary<Type, object> LeftAlignment = new();
    private static readonly string[] TierNames = { "TierA", "TierB", "TierC", "TierD", "TierE" };

    private static Accessor Resolve(Type type, string name)
    {
        if (Accessors.TryGetValue((type, name), out var known)) return known;
        var property = type.GetProperty(name, Members);
        var field = property == null ? type.GetField(name, Members) ?? throw new MissingMemberException(type.FullName, name) : null;
        return Accessors[(type, name)] = new Accessor(property, field);
    }
    private static object? Get(object target, string name)
    {
        var type = target as Type ?? target.GetType(); var instance = target is Type ? null : target;
        var accessor = Resolve(type, name);
        return accessor.Property != null ? accessor.Property.GetValue(instance) : accessor.Field!.GetValue(instance);
    }
    private static void Set(object target, string name, object? value)
    {
        var type = target as Type ?? target.GetType(); var instance = target is Type ? null : target;
        var accessor = Resolve(type, name);
        if (accessor.Property != null) accessor.Property.SetValue(instance, value);
        else accessor.Field!.SetValue(instance, value);
    }
    /// <summary>Writes only when the native value differs: a redundant write to a TextMeshPro member marks the mesh dirty.</summary>
    private static void SetIfChanged(object target, string name, object value)
    {
        if (!Equals(Get(target, name), value)) Set(target, name, value);
    }
    private static object? Call(object target, string name, params object?[] args)
    {
        var type = target as Type ?? target.GetType();
        if (!MethodCandidates.TryGetValue((type, name, args.Length), out var candidates))
            MethodCandidates[(type, name, args.Length)] = candidates = type.GetMethods(Members)
                .Where(candidate => candidate.Name == name && !candidate.IsGenericMethodDefinition
                    && candidate.GetParameters().Length == args.Length).ToArray();
        MethodInfo? method = null;
        foreach (var candidate in candidates)
        {
            var parameters = candidate.GetParameters();
            bool match = true;
            for (int index = 0; index < parameters.Length && match; index++)
                match = args[index] == null || parameters[index].ParameterType.IsInstanceOfType(args[index]);
            if (!match) continue;
            if (method != null)
                throw new InvalidOperationException("More than one " + type.FullName + "." + name + " overload takes these " + args.Length + " arguments.");
            method = candidate;
        }
        if (method == null) throw new InvalidOperationException("No " + type.FullName + "." + name + " overload takes these " + args.Length + " arguments.");
        return method.Invoke(target is Type ? null : target, args);
    }
    internal static void Report(RundownDiagnostic value)
    {
        _refusePlans?.Invoke(value);
        _report(value.Code + " " + value.Detail);
        if (!MapLog.Logs(RuntimeLogLevel.Error)) return;
        MapLog.Record(RuntimeLogLevel.Error, value.Code, value.Detail,
            RuntimeLogValue.Of("package", value.Package), RuntimeLogValue.Of("entry", value.Entry), RuntimeLogValue.Of("other", value.Other),
            RuntimeLogValue.Of("slot", value.Slot));
    }

    // Read before a generation's Rundowns are published, after native content consumers finish. A lifecycle observer cannot
    // authorize these writes; GameDataInit only establishes the host's readiness gate. Rows <paramref name="owner"/> published (the
    // current generation's own Rundown rows, which the new generation replaces or removes) are not occupied.
    internal static IReadOnlyList<ContentVanillaId> ObserveTables(string? owner = null)
    {
        var occupied = new List<ContentVanillaId>();
        var own = owner == null ? new HashSet<(string, uint)>()
            : NativeDataBlockTables.OwnedRows().Where(row => row.Owner == owner).Select(row => (row.Type, row.Id)).ToHashSet();
        // Fixed-only Item references do not require a package-owned Item block, so always observe this table.
        Tables["Item"] = NativeType("GameData.ItemDataBlock");
        foreach (var (name, type) in Tables)
        {
            object blocks = Get(Get(type, "Wrapper")!, "Blocks")!;
            int count = (int)Get(blocks, "Count")!;
            for (int i = 0; i < count; i++)
            {
                var block = Call(blocks, "get_Item", i)!;
                uint id = (uint)Get(block, "persistentID")!;
                if (own.Contains((name, id))) continue;
                occupied.Add(new(name, id, "loaded:" + name + "/" + Get(block, "name") + "#" + id));
            }
        }
        return Array.AsReadOnly(occupied.ToArray());
    }

    /// <summary>Validates this menu's packs against <paramref name="content"/> and publishes each: <paramref name="publish"/> receives the
    /// pack and its materialized native rows and writes them (a throw refuses that pack alone); every refusal goes to
    /// <paramref name="report"/>. Nothing of the menu is written here.</summary>
    internal RundownPlacementAdmission Publish(AdmittedContentSnapshot content, IReadOnlyList<RundownPack> installed,
        IReadOnlyList<ContentVanillaId> observed, Action<RundownDiagnostic> report,
        Action<RundownPack, IReadOnlyList<NativeDataBlockRegistration>> publish)
    {
        if (_installed || _loaded.Count != 0) throw new InvalidOperationException("A Rundown menu publishes once.");
        var published = RundownPlacementAdmission.Publish(content, installed, observed, pack =>
        {
            ValidatePlayerValues(pack);
            publish(pack, pack.Blocks.Select(block =>
            {
                string json = content.Materialize(block, pack);
                return new NativeDataBlockRegistration(block.Type, StrictJson.BlockName((JsonObject)JsonNode.Parse(json)!, block.Id),
                    block.PersistentId, json);
            }).ToArray());
        }, report);
        _loaded = published.Packs;
        // The achievement names answer only for Rundowns that were published.
        _achievementRundowns = AchievementRundowns(_loaded);
        return published;
    }

    /// <summary>Makes this the menu: the menu the callbacks answered through until now puts its writes back first, then this one
    /// writes the load list and the selected Rundown (each write keeps the value it found, <see cref="ContentLease.Overwrite"/>) and shows
    /// its names. A Rundown the player had selected stays selected when this menu still offers it.</summary>
    internal void Install()
    {
        if (_installed) return;
        uint live = (uint)Get(NativeType("Globals.Global"), "RundownIdToLoad")!;
        Current?.Uninstall();
        _installed = true;
        Current = this;
        if (_loaded.Count > 0) WriteSetup(_loaded, live);
        ReadNames();
    }

    /// <summary>Puts back everything this menu wrote (load list, selection, level names, player values), newest first. Idempotent.</summary>
    internal void Uninstall()
    {
        if (!_installed) return;
        _installed = false;
        var failures = new List<Exception>();
        foreach (var name in LevelNames.Values)
            try { name.Level.Descriptive.PublicName = name.Primary; } catch (Exception error) { failures.Add(error); }
        LevelNames.Clear();
        try { RestorePlayerValues(); } catch (Exception error) { failures.Add(error); }
        for (int index = _patches.Count - 1; index >= 0; index--)
            try { _patches[index].Revoke(); } catch (Exception error) { failures.Add(error); }
        _patches.Clear();
        if (ReferenceEquals(Current, this)) Current = null;
        if (failures.Count != 0) throw new AggregateException("The Rundown menu could not put back every value it wrote.", failures);
    }

    /// <summary>The Rundown page as it stands is laid out from the previous generation's blocks: the selection buttons and the open
    /// Rundown's tiers are placed again from the tables as they are now.</summary>
    internal static void PlaceAgain()
    {
        var page = GuiManager.MainMenuLayer?.PageRundownNew;
        if (page == null) return;
        var buttons = page.m_rundownSelections;
        var active = Globals.Global.ActiveRundownIds;
        if (buttons != null && active != null)
            for (int index = 0; index < buttons.Count && index < active.Length; index++)
                if (buttons[index] != null) page.UpdateRundownSelectionButton(buttons[index], active[index]);
        if (page.m_currentRundownData == null) return;
        var data = GameData.RundownDataBlock.GetBlock(Globals.Global.RundownIdToLoad);
        if (data == null) return;
        page.ResetRundownItems();
        // PlaceRundown spawns a fresh intel button and ResetRundownItems leaves the old one standing.
        if (page.m_rundownIntelButton != null) UnityEngine.Object.Destroy(page.m_rundownIntelButton.gameObject);
        page.PlaceRundown(data);
    }
    // Level and Rundown names. A level name (Descriptive.PublicName) ships as the author's primary-language text, and the
    // package's levelNames lists the Text block holding every language. The field gets this machine's language column, again
    // whenever the game sets its language; an empty column keeps the primary text. The game also hashes PublicName into the
    // level checksum the host compares before a player may build the level, so while it hashes (LevelNameChecksum) the field
    // holds the shipped primary text again: every machine hashes the same bytes, with or without this hook or this version.
    // Without the hook the field is never rewritten. A screen that copied a name earlier keeps that language until it reads
    // the field again. The Rundown title is the game's own localized text; only the menu button, which this class writes, reads it here.
    private sealed class LevelName
    {
        internal LevelName(GameData.ExpeditionInTierData level, string primary, uint text) { Level = level; Primary = primary; Text = text; Shown = primary; }
        internal readonly GameData.ExpeditionInTierData Level;
        internal readonly string Primary;
        internal readonly uint Text;
        internal string Shown;
    }
    private readonly Dictionary<IntPtr, LevelName> LevelNames = new();
    private readonly Dictionary<uint, string> Titles = new();
    private static readonly HashSet<string> UnresolvedNames = new(StringComparer.Ordinal);
    private const string LevelNameCode = "rundown.level-name-unresolved";

    private static void ShowCurrentNames() => Current?.ShowNames();

    private void ReadNames()
    {
        if (_namesPerLanguage)
            foreach (var pack in _loaded)
                foreach (var row in pack.LevelNames)
                {
                    string where = pack.Package + " " + row.Key.Tier + (row.Key.Index + 1);
                    try
                    {
                        var block = GameData.RundownDataBlock.GetBlock(pack.RundownId);
                        var tier = row.Key.Tier switch
                        {
                            'A' => block?.TierA,
                            'B' => block?.TierB,
                            'C' => block?.TierC,
                            'D' => block?.TierD,
                            _ => block?.TierE
                        };
                        var level = tier != null && row.Key.Index < tier.Count ? tier[row.Key.Index] : null;
                        if (level?.Descriptive?.PublicName is { } primary) LevelNames[level.Pointer] = new LevelName(level, primary, row.Value);
                        else Unresolved(where, "no such level");
                    }
                    catch (Exception error) { Unresolved(where, error.GetBaseException().Message); }
                }
        ShowNames();
    }

    private void ShowNames()
    {
        var current = Localization.Text.TextLocalizationService?.CurrentLanguage ?? Localization.Language.English;
        foreach (var name in LevelNames.Values)
        {
            if (!ForgeUI.GameText.TryColumn(name.Text, current, out var shown, out var error))
                Unresolved("Text " + name.Text, error?.GetBaseException().Message ?? "no Text block");
            name.Shown = shown.Length > 0 ? shown : name.Primary;
            name.Level.Descriptive.PublicName = name.Shown;
        }
        foreach (var pack in _loaded)
        {
            string title = "";
            try { title = ForgeUI.GameText.Read(GameData.RundownDataBlock.GetBlock(pack.RundownId)?.StorytellingData?._Title_k__BackingField).Trim(); }
            catch (Exception error) { Unresolved("title " + pack.RundownId, error.GetBaseException().Message); }
            Titles[pack.RundownId] = title.Length > 0 ? title : pack.Title;
        }
    }

    private static void Unresolved(string name, string reason)
    {
        if (!UnresolvedNames.Add(name) || !MapLog.Logs(RuntimeLogLevel.Error)) return;
        MapLog.Record(RuntimeLogLevel.Error, LevelNameCode, "A level or Rundown name could not be shown in the game's language; it keeps the text the package shipped.",
            RuntimeLogValue.Quote("name", name), RuntimeLogValue.Quote("reason", reason));
    }

    /// <summary>The checksum hook: while the game hashes a level, its name is the shipped primary text every machine shares.</summary>
    internal static void ChecksumName(GameData.ExpeditionInTierData level, bool hashing)
    {
        if (Current is not { } menu || menu.LevelNames.Count == 0 || !menu.LevelNames.TryGetValue(level.Pointer, out var name)) return;
        level.Descriptive.PublicName = hashing ? name.Primary : name.Shown;
    }

    private static string Title(RundownPack pack) => Current != null && Current.Titles.TryGetValue(pack.RundownId, out var title) ? title : pack.Title;

    private static IReadOnlyDictionary<uint, RundownName> AchievementRundowns(IReadOnlyList<RundownPack> packs)
        => packs.ToDictionary(pack => pack.RundownId, pack => (RundownName)(int)pack.RundownId);
    // Build 20403457 implements these two native methods as closed switches: id -> RundownName and the inverse
    // RundownName -> RundownManager.GetLocalRundownKey(id). There is no dynamic vanilla AddName API. Extend that
    // same round trip only for successfully admitted Forge Rundowns; every unknown/non-Forge value still executes
    // the original method and retains its original error log.
    internal static bool SelectAchievementRundownName(ref RundownName __result)
    {
        if (Current is not { } menu || !menu._achievementRundowns.TryGetValue(Globals.Global.RundownIdToLoad, out var registered)) return true;
        __result = registered;
        return false;
    }
    internal static bool SelectAchievementRundownKey(RundownName __0, ref string __result)
    {
        uint id = unchecked((uint)(int)__0);
        if (Current is not { } menu || !menu._achievementRundowns.TryGetValue(id, out var registered) || registered != __0) return true;
        __result = RundownManager.GetLocalRundownKey(id);
        return false;
    }
    private void WriteSetup(IReadOnlyList<RundownPack> packs, uint live)
    {
        const string Owner = "map-menu";
        var blocks = Get(Get(Tables["GameSetup"], "Wrapper")!, "Blocks")!;
        if ((int)Get(blocks, "Count")! == 0) throw new InvalidOperationException("No loaded GameSetup block for Rundown admission.");
        var global = NativeType("Globals.Global");
        uint selected = (uint)Get(global, "RundownIdToLoad")!;
        uint[]? numbers = null;
        // A single-Rundown package owns the whole menu (SelectSlots admits no other package beside it): the native
        // load list holds only its ID, which is how the original page is told to offer exactly one Rundown.
        var single = packs.FirstOrDefault(IsSingle);
        // A Forge Rundown is custom content: its progress is this machine's records (ForgeCore Progression), not the drop server's.
        foreach (var pack in packs) ForgeCore.Progression.SuppliedByMod(pack.RundownId);
        static uint[] Read(object ids) => Enumerable.Range(0, (int)Get(ids, "Count")!).Select(index => (uint)Call(ids, "get_Item", index)!).ToArray();
        static void Write(object ids, uint[] values)
        {
            Call(ids, "Clear");
            foreach (var value in values) Call(ids, "Add", value);
        }
        for (int i = 0; i < (int)Get(blocks, "Count")!; i++)
        {
            var ids = Get(Call(blocks, "get_Item", i)!, "RundownIdsToLoad")!;
            var list = Read(ids);
            if (single != null)
            {
                selected = single.RundownId;
                list = new[] { single.RundownId };
            }
            else
            {
                if (list.Length != 8) throw new InvalidOperationException("Expected the original eight Rundown menu slots.");
                foreach (var pack in packs)
                {
                    if (pack.MenuSlot is < 1 or > 8) throw new InvalidOperationException("Invalid admitted Rundown menu slot.");
                    if (selected == list[pack.MenuSlot - 1]) selected = pack.RundownId;
                    list[pack.MenuSlot - 1] = pack.RundownId;
                }
            }
            _patches.Add(ContentLease.Overwrite(Owner, () => Read(ids), values => Write(ids, values), list));
            numbers ??= list;
        }
        // The Rundown the player had selected stays selected when this menu still offers it.
        if (numbers!.Contains(live)) selected = live;
        // StartMainGame.Start has already copied the list into a native array and selected its last ID.
        // Editing the DataBlock cannot update that snapshot. Write the same list into both copies.
        var arrayType = global.GetProperty("ActiveRundownIds", Members)!.PropertyType;
        _patches.Add(ContentLease.Overwrite<object?>(Owner, () => Get(global, "ActiveRundownIds"), value => Set(global, "ActiveRundownIds", value),
            Activator.CreateInstance(arrayType, new object[] { numbers! })));
        _patches.Add(ContentLease.Overwrite(Owner, () => (uint)Get(global, "RundownIdToLoad")!, value => Set(global, "RundownIdToLoad", value), selected));
    }
    internal static void MenuStarting(object __instance)
    {
        if (Loaded.Count == 0) return;
        var buttons = Get(__instance, "m_rundownSelections");
        if (buttons == null) return; // Serialized menu controls may only become available during Setup.
        int capacity = (int)Get(buttons, "Count")!;
        if (capacity != _capacity)
        {
            _capacity = capacity;
            if (capacity != 8) throw new InvalidOperationException("Expected eight native Rundown menu buttons.");
            if (MapLog.Logs(RuntimeLogLevel.Info))
            {
                var active = Get(NativeType("Globals.Global"), "ActiveRundownIds")!;
                int visible = Convert.ToInt32(Get(active, "Length"));
                string ids = string.Join(",", Enumerable.Range(0, visible).Select(index => (uint)Call(active, "get_Item", index)!));
                foreach (var pack in Loaded)
                    MapLog.Record(RuntimeLogLevel.Info, AdmittedCode,
                        "package=" + pack.Package + " rundownId=" + pack.RundownId + " rundownIdsToLoad=[" + ids
                        + "] capacity=" + capacity + " visible=" + visible + " slot=" + pack.MenuSlot + (IsSingle(pack) ? " single=true" : ""),
                        RuntimeLogValue.Quote("package", pack.Package), RuntimeLogValue.Quote("entry", pack.Rundown),
                        RuntimeLogValue.Of("rundownId", pack.RundownId), RuntimeLogValue.Of("rundownIdsToLoad", ids),
                        RuntimeLogValue.Of("capacity", capacity), RuntimeLogValue.Of("visible", visible), RuntimeLogValue.Of("slot", pack.MenuSlot));
            }
        }
    }
    internal static bool BindSelection(object __instance, object __0, ref uint __1)
    {
        if (Loaded.Count == 0) return true;
        MenuStarting(__instance);
        var buttons = Get(__instance, "m_rundownSelections")!;
        int index = (int)Call(buttons, "IndexOf", __0)!;
        var pack = Loaded.FirstOrDefault(pack => ButtonPosition(pack) == index + 1);
        if (pack == null) return true;
        // The installed HarmonyX does not propagate edits to its __args snapshot to the original call.
        // A ref argument changes the value the native method captures as rundownId and rundownData.
        __1 = pack.RundownId;
        return true;
    }
    internal static void SelectionTitle(object __0, uint __1)
    {
        var pack = Loaded.FirstOrDefault(pack => pack.RundownId == __1);
        if (pack != null) SetTitle(__0, Title(pack));
    }
    internal static void RefreshTitles(object __instance)
    {
        if (Loaded.Count == 0) return;
        var buttons = Get(__instance, "m_rundownSelections")!;
        foreach (var pack in Loaded)
            SetTitle(Call(buttons, "get_Item", ButtonPosition(pack) - 1)!, Title(pack));
    }
    // The single-Rundown package is the only entry of the load list, so it is the page's first button.
    private static int ButtonPosition(RundownPack pack) => IsSingle(pack) ? 1 : pack.MenuSlot;
    // MenuSlot 0 is the single-Rundown package (menuMode "single"), which owns the whole menu.
    private static bool IsSingle(RundownPack pack) => pack.MenuSlot == 0;
    private static void SetTitle(object button, string title)
    {
        var text = Get(button, "m_rundownText")
            ?? throw new InvalidOperationException("Rundown selection button " + (Get(button, "RundownKey") ?? "?")
                + " has no m_rundownText, so its title has nowhere to be written.");
        // Not every selection button of the page carries the ALT:// prefix: one without it has only the title text,
        // which then keeps its native rectangle and receives the name as it is.
        if (Get(button, "m_altText") is not { } prefix)
        {
            SetIfChanged(text, "text", title);
            return;
        }
        var prefixRect = Get(prefix, "rectTransform")!;
        var titleRect = Get(text, "rectTransform")!;
        var prefixPosition = Get(prefixRect, "localPosition")!;
        float prefixScale = (float)Get(Get(prefixRect, "localScale")!, "x")!;
        float titleScale = (float)Get(Get(titleRect, "localScale")!, "x")!;
        float width = (float)Get(Get(prefixRect, "sizeDelta")!, "x")!;
        float measured = (float)Get(prefix, "preferredWidth")!;
        float gap = (float)Get(Call(prefix, "GetPreferredValues", "M")!, "x")! * .25f;
        float available = (width - measured) * prefixScale * .5f - gap * prefixScale;
        if (prefixScale <= 0 || titleScale <= 0 || available <= 0)
            throw new InvalidOperationException("Rundown title has no space after the native ALT prefix.");
        // Both original text elements share a parent. Keep ALT's text, color, position and visibility;
        // give the full name its own rectangle to the right, using the native secondary label's font size.
        // This runs from the page's per-frame text refresh: every member is compared first and written only when
        // it differs, so a settled title costs reads but no native writes or mesh rebuilds.
        var position = Get(titleRect, "localPosition")!;
        float targetX = (float)Get(prefixPosition, "x")! + measured * prefixScale * .5f + gap * prefixScale + available * .5f;
        var targetY = Get(prefixPosition, "y");
        if (!Equals(Get(position, "x"), targetX) || !Equals(Get(position, "y"), targetY))
        {
            Set(position, "x", targetX);
            Set(position, "y", targetY);
            Set(titleRect, "localPosition", position);
        }
        var size = Get(titleRect, "sizeDelta")!;
        float targetWidth = available / titleScale;
        if (!Equals(Get(size, "x"), targetWidth))
        {
            Set(size, "x", targetWidth);
            Set(titleRect, "sizeDelta", size);
        }
        float fontSize = (float)Get(prefix, "fontSize")!;
        SetIfChanged(text, "fontSizeMin", fontSize * .5f); SetIfChanged(text, "fontSizeMax", fontSize);
        SetIfChanged(text, "enableAutoSizing", true); SetIfChanged(text, "enableWordWrapping", false); SetIfChanged(text, "richText", false);
        var alignmentType = Get(text, "alignment")!.GetType();
        var left = LeftAlignment.GetOrAdd(alignmentType, static type => Enum.Parse(type, "Left"));
        SetIfChanged(text, "alignment", left);
        SetIfChanged(text, "text", title);
    }
    internal static bool SelectPlayerValues(object __instance, object __0)
    {
        if (Loaded.Count == 0) return true;
        MenuStarting(__instance);
        uint id = (uint)Get(__0, "persistentID")!;
        ApplyPlayerValues(id);
        return true;
    }
    // Build 20403457: PlaceRundown allocates m_expIconHidden at 0x1264DBA, but fills it only at
    // 0x1265A0B..0x1265AAF. AddVanityItemDropExpedition indexes it by the native expedition key at
    // 0x1261A81..0x1261A9E; UpdateVanityItemUnlocks reads Global.RundownIdToLoad, not the page's selection.
    // AddVanityItemDropLayer also dereferences RundownProgression.Expeditions and every value (0x1261F30..
    // 0x1261FAB). PlaceRundown can finish before that snapshot arrives. Retain the receipt until both are ready.
    // This also protects pages with no admitted Forge pack, without issuing or replacing any request.
    private static bool ProgressionReady(object page, out string missingExpedition, out string reason)
    {
        missingExpedition = "";
        reason = "page-icons-not-ready";
        var data = Get(page, "m_currentRundownData");
        var hidden = Get(page, "m_expIconHidden");
        if (data == null || hidden == null || Get(page, "m_expIconsAll") == null
            || (uint)Get(data, "persistentID")! != Globals.Global.RundownIdToLoad) return false;
        reason = "rundown-tree-hidden";
        if ((bool)Get(data, "NeverShowRundownTree")!) return false;
        reason = "active-rundown-not-ready";
        var active = GameData.RundownDataBlock.GetBlock(Globals.Global.RundownIdToLoad);
        if (active == null) return false;
        reason = "vanity-layer-data-not-ready";
        var layers = GameData.VanityItemsLayerDropsDataBlock.GetBlock(active.VanityItemLayerDropDataBlock);
        if (active.VanityItemLayerDropDataBlock != 0 && layers == null) return false;
        if (layers != null)
        {
            var drops = layers.LayerDrops;
            if (drops == null) return false;
            foreach (var drop in drops)
                if (drop == null || drop.Groups == null) return false;
            if (drops.Count > 0)
            {
                reason = "vanity-progression-not-ready";
                var expeditions = RundownManager.RundownProgression?.Expeditions;
                if (expeditions == null) return false;
                reason = "vanity-progression-entry-not-ready";
                foreach (var expedition in expeditions.Values)
                    if (expedition == null) return false;
            }
        }
        reason = "vanity-expedition-data-not-ready";
        for (int tier = 1; tier <= 5; tier++)
        {
            var expeditions = Get(active, TierNames[tier - 1]);
            if (expeditions == null) return false;
            int count = (int)Get(expeditions, "Count")!;
            for (int index = 0; index < count; index++)
            {
                var expedition = Call(expeditions, "get_Item", index);
                if (expedition == null) return false;
                var drops = Get(expedition, "VanityItemsDropData");
                var groups = drops == null ? null : Get(drops, "Groups");
                if (groups == null) return false;
                if ((int)Get(groups, "Count")! == 0) continue;
                string key = RundownManager.GetRundownProgressionExpeditionKey((eRundownTier)tier, index);
                if ((bool)Call(hidden, "ContainsKey", key)!) continue;
                reason = "vanity-expedition-icon-not-ready";
                missingExpedition = key;
                return false;
            }
        }
        reason = "ready";
        return true;
    }

    private static bool _unlockWritten;

    /// <summary>The unlock-all setting (<see cref="ForgeEngine.RuntimeSettings.UnlockAllLevels"/>) as the game's own full-Rundown switch
    /// <c>Global.AllowFullRundown</c>, which <c>UpdateTierIconsWithProgression</c> ORs into every level's and tier marker's unlock. Written
    /// at the start of the page's progress handler, right before the page reads it: <c>GlobalSetup.Setup</c> writes the switch back at
    /// startup, so a value set at load would not last. Off writes false only after this has written true, so the switch is otherwise the
    /// game's. No clear is invented and nothing is written to the progress file.</summary>
    internal static void ApplyUnlockAll()
    {
        bool on = ForgeEngine.RuntimeSettings.UnlockAllLevels?.Value == true;
        if (!on && !_unlockWritten) return;
        Globals.Global.AllowFullRundown = on;
        _unlockWritten = on;
    }

    internal static bool ProgressionReceived(object __instance)
    {
        var page = (IntPtr)Get(__instance, "Pointer")!;
        if (ProgressionReady(__instance, out string missingExpedition, out string reason))
        {
            DeferredProgression.Remove(page);
            return true;
        }
        uint rundownId = Globals.Global.RundownIdToLoad;
        bool changed = !DeferredProgression.TryGetValue(page, out var pending) || pending.RundownId != rundownId
            || pending.Reason != reason || pending.MissingExpedition != missingExpedition;
        DeferredProgression[page] = (__instance, rundownId, reason, missingExpedition);
        Set(__instance, "m_rundownProgressionReceived", true);
        if (changed && MapLog.Logs(RuntimeLogLevel.Info))
        {
            var selected = Get(__instance, "m_currentRundownData");
            MapLog.Record(RuntimeLogLevel.Info, "rundown.progression-deferred", "Progression retained until the active Rundown page and native vanity data are ready.",
                RuntimeLogValue.Of("phase", "deferred"), RuntimeLogValue.Of("reason", reason),
                RuntimeLogValue.Of("selected", selected != null), RuntimeLogValue.Of("hiddenReady", Get(__instance, "m_expIconHidden") != null),
                RuntimeLogValue.Of("iconsReady", Get(__instance, "m_expIconsAll") != null),
                RuntimeLogValue.Of("selectedRundownId", selected == null ? 0u : (uint)Get(selected, "persistentID")!),
                RuntimeLogValue.Of("activeRundownId", Globals.Global.RundownIdToLoad), RuntimeLogValue.Of("missingExpedition", missingExpedition));
        }
        return false;
    }
    internal static void ProgressionPlaced(object __instance, object __0)
    {
        // Native PlaceRundown returns before creating controls when NeverShowRundownTree is set.
        if (DeferredProgression.Count == 0 || (bool)Get(__0, "NeverShowRundownTree")!
            || !DeferredProgression.ContainsKey((IntPtr)Get(__instance, "Pointer")!)
            || (uint)Get(__0, "persistentID")! != Globals.Global.RundownIdToLoad) return;
        ProgressionUpdated(__instance);
    }
    /// <summary>After the native placement: tiers too crowded for their native spacing are laid out in rows (<see cref="RundownTierLayout"/>).
    /// Like the other menu callbacks it answers only once a pack was admitted; native PlaceRundown places no icons when NeverShowRundownTree is set.</summary>
    internal static void ArrangeTiers(object __instance, object __0)
    {
        if (Loaded.Count == 0 || (bool)Get(__0, "NeverShowRundownTree")!) return;
        RundownTierLayout.Arrange((CellMenu.CM_PageRundown_New)__instance);
    }
    internal static void ProgressionUpdated(object __instance)
    {
        // Use the page's existing native lifecycle, only while it owns a receipt. Read the current wrapper and
        // current selection each time; no cached progression, request, coroutine or second scheduler.
        if (DeferredProgression.Count == 0 || !DeferredProgression.ContainsKey((IntPtr)Get(__instance, "Pointer")!)
            || !ProgressionReceived(__instance)) return;
        if (MapLog.Logs(RuntimeLogLevel.Info))
            MapLog.Record(RuntimeLogLevel.Info, "rundown.progression-deferred", "Replaying retained progression after the page and native vanity data became ready.",
                RuntimeLogValue.Of("phase", "replay"), RuntimeLogValue.Of("reason", "ready"),
                RuntimeLogValue.Of("activeRundownId", Globals.Global.RundownIdToLoad));
        // The receiver's prefix consumes the pending receipt before its original body runs. Never synthesize
        // a new request or replay a cached payload; vanilla reads the latest progression for this selection.
        Call(__instance, "OnRundownProgressionFileUpdated");
    }
    internal static void ExpeditionPlayerValues()
    {
        if (Loaded.Count == 0) return;
        ApplyPlayerValues(RundownIdForKey(RundownManager.ActiveRundownKey));
    }
    // Resolve only the current admitted registration. Unknown and pre-release keys restore the baseline;
    // they never enter a legacy name parser or select another package's player values.
    internal static uint RundownIdForKey(string key)
        => Loaded.FirstOrDefault(pack => string.Equals(RundownManager.GetLocalRundownKey(pack.RundownId), key,
            StringComparison.Ordinal))?.RundownId ?? 0u;
    private static void ValidatePlayerValues(RundownPack pack)
    {
        if (pack.PlayerOverrides == null) return;
        object player = Call(Tables["Player"], "GetBlock", 1u)!;
        foreach (var row in pack.PlayerOverrides)
        {
            object owner = player;
            foreach (var part in row.Key.Split('.')) owner = Get(owner, part) ?? throw new InvalidOperationException("Missing player field " + row.Key);
            if (owner is not (float or int or bool)) throw new InvalidOperationException("Unsupported player field " + row.Key);
            _ = Convert.ChangeType(row.Value!.ToString(), owner.GetType(), System.Globalization.CultureInfo.InvariantCulture);
        }
    }
    /// <summary>The player values a menu wrote go back to the values they replaced (a menu leaving: Uninstall).</summary>
    private static void RestorePlayerValues()
    {
        if (PlayerWritten.Count != 0) ApplyPlayerValues(0u, nothing: true);
    }
    private static void ApplyPlayerValues(uint id, bool nothing = false)
    {
        var pack = nothing ? null : Loaded.FirstOrDefault(pack => pack.RundownId == id);
        object player = Call(Tables["Player"], "GetBlock", 1u)!;
        object Value(string path)
        {
            object value = player;
            foreach (string part in path.Split('.')) value = Get(value, part)!;
            return value;
        }
        foreach (var (path, written) in PlayerWritten)
        {
            // A later vanilla / Forge write owns its new value. Revert only our still-present contribution.
            if (Equals(Value(path), written)) SetPlayerPath(player, path.Split('.'), 0, PlayerBaseline[path]);
        }
        PlayerWritten.Clear();
        PlayerBaseline.Clear();
        if (pack?.PlayerOverrides == null) return;
        foreach (var value in pack.PlayerOverrides)
        {
            var current = Value(value.Key);
            var written = Convert.ChangeType(value.Value!.ToString(), current.GetType(), System.Globalization.CultureInfo.InvariantCulture);
            if (Equals(current, written)) continue; // A no-op is not a field this package wrote.
            PlayerBaseline[value.Key] = current;
            PlayerWritten[value.Key] = written;
            SetPlayerPath(player, value.Key.Split('.'), 0, written);
        }
    }
    private static void SetPlayerPath(object owner, string[] parts, int index, object? value)
    {
        if (index == parts.Length - 1) { Set(owner, parts[index], value); return; }
        var child = Get(owner, parts[index])!;
        SetPlayerPath(child, parts, index + 1, value);
        // ActionCost is a value type: write the updated value back through every parent.
        Set(owner, parts[index], child);
    }
}
