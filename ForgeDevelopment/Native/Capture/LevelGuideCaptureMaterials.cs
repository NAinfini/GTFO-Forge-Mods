using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using GameData;
using Enemies;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using LevelGeneration;
using UnityEngine;

namespace ForgeDevelopment.Native;

// The driver supplies requests from the catalog's capture metadata. No value/probe list lives here.
// patch-targets: observation (CaptureHarmonyGroup puts every callback on read-only through ForgeCore.Hooks.Observe, beside each method's owner)
internal static class LevelGuideCaptureMaterials
{
    internal sealed class Request
    {
        public string Id { get; set; } = "";
        public string Component { get; set; } = "";
        public string Field { get; set; } = "";
        public string When { get; set; } = "";
        public string[] Methods { get; set; } = Array.Empty<string>();
        public string[] HookComponents { get; set; } = Array.Empty<string>();
        public string[] DataBlockPaths { get; set; } = Array.Empty<string>();
        public string KeyType { get; set; } = "";
        public string CatalogComponent { get; set; } = "";
        public string[] Members { get; set; } = Array.Empty<string>();
        public Subject[] Subjects { get; set; } = Array.Empty<Subject>();
    }
    internal sealed class Subject { public string Type { get; set; } = ""; public uint Id { get; set; } }
    internal sealed class Plan
    {
        public bool Enabled { get; set; }
        public string SourceSha256 { get; set; } = "";
        public bool Abilities { get; set; }
        public bool PopulationKeys { get; set; }
        public string BuildId { get; set; } = "";
        public string GameBuild { get; set; } = "";
        public string CaptureRunId { get; set; } = "";
        public Request[] Requests { get; set; } = Array.Empty<Request>();
        public Dictionary<string, string[]> NativeFields { get; set; } = new();
        public string[] BeforeOwners { get; set; } = Array.Empty<string>();
    }
    private static Plan? _plan;
    private static bool _inventoryDone, _proofStarted;
    private static readonly CaptureHarmonyGroup CaptureHooks = new("NAinfini.ForgeDevelopment.MaterialCapture");
    private static string? _planText;
    private static CachedCapturePlan<Plan>? _planFile;
    private static bool _inactiveClean;
    internal static bool Recording => _plan != null;
    private static string? _planError;
    private static readonly CapturePlanAttemptGate Attempts = new();
    private static bool _populationStarted, _populationRecorded;
    private static bool _recordBudgetReported;
    private static readonly Dictionary<string, Request[]> Hooks = new();
    private static readonly HashSet<string> Observed = new(StringComparer.Ordinal);
    private static readonly HashSet<string> UnresolvedHooks = new();
    private static string PlanPath => Path.Combine(DevelopmentPaths.Root, "material-capture.forge");
    // A prepared capture plan is an explicit startup consumer. Idle Authoring does not create its watcher.
    internal static bool Requested
    {
        get
        {
            var plan = ReadPlanFile(out _, out var error);
            if (error != null) Plugin.PluginLog.LogError("Material capture plan refused: " + error);
            return plan != null;
        }
    }
    private static Plan? ReadPlan(out string? text)
    {
        _planFile ??= new CachedCapturePlan<Plan>(PlanPath, ReadPlanFile);
        var plan = _planFile.Read(out text, out var error);
        if (error != null && _planError != error) Note("plan-unavailable", error);
        _planError = error;
        return plan;
    }

    private static Plan? ReadPlanFile(out string? text, out string? error)
        => CapturePlanFile.Read<Plan>(PlanPath,
            CaptureFiles.Json, value => value.Enabled, Validate, out text, out error);

    internal static void StopPlanWatch() { _planFile?.Dispose(); _planFile = null; }
    private static void Validate(Plan value)
    {
        if (value.Requests == null || value.NativeFields == null || value.BeforeOwners == null
            || value.SourceSha256 == null || value.BuildId == null
            || value.GameBuild == null || value.CaptureRunId == null
            || value.NativeFields.Any(pair => pair.Value == null || pair.Value.Any(string.IsNullOrEmpty))
            || value.Requests.Any(request => request == null
                || request.Component == null || request.Field == null || request.Id == null
                || request.Methods == null || request.Methods.Any(method => string.IsNullOrEmpty(method))
                || request.When == null || request.KeyType == null || request.CatalogComponent == null
                || request.HookComponents == null || request.HookComponents.Any(string.IsNullOrEmpty)
                || request.DataBlockPaths == null || request.DataBlockPaths.Any(string.IsNullOrEmpty)
                || request.Members == null || request.Members.Any(string.IsNullOrEmpty)
                || request.Subjects == null || request.Subjects.Any(subject => subject == null || string.IsNullOrEmpty(subject.Type)))
            || value.BeforeOwners.Any(string.IsNullOrEmpty))
            throw new InvalidDataException("Material capture plan contains invalid request fields.");
    }
    // Runs every authoring frame; a plan that disappears revokes the old hooks.
    internal static void Tick()
    {
        var plan = ReadPlan(out var text);
        if (plan == null && _inactiveClean && CaptureHooks.Fault == null) return;
        if (plan != null) _inactiveClean = false;
        if (plan == null || text != _planText || CaptureHooks.Fault != null)
        {
            if (!Stop())
            {
                if (plan != null) Attempts.Failed(text!);
                return;
            }
            if (plan == null) { _inactiveClean = true; Attempts.Disabled(); return; }
            if (!Attempts.CanInstall(text!)) return;
            _plan = plan;
            _planText = text;
        }
        else plan = _plan!;
        if (!CaptureHooks.Installed)
        {
            try
            {
                CaptureHooks.Install(() => InstallHooks(plan));
                Note("installed", "requests=" + plan.Requests.Length + "; methods=" + Hooks.Count + "; source=" + plan.SourceSha256);
            }
            catch (Exception error) { Attempts.Failed(text!); Note("install-failed", error.Message + "; this plan generation is blocked; process-terminal native failures require a game restart"); return; }
        }
        try { TickInventory(plan); }
        catch (Exception error) { Attempts.Failed(text!); Note("capture-tick-failed", error.Message); Stop(); }
    }
    internal static bool Stop()
    {
        if (!CaptureHooks.Stop())
        {
            Note("cleanup-failed", CaptureHooks.Fault ?? "");
            return false;
        }
        _plan = null; _planText = null;
        Hooks.Clear(); Observed.Clear(); UnresolvedHooks.Clear();
        _inventoryDone = _populationStarted = _populationRecorded = _recordBudgetReported = false;
        _costCapture = null;
        return true;
    }
    private static void InstallHooks(Plan plan)
    {
        if (!_proofStarted) { CaptureVanillaProof.Start(); _proofStarted = true; }
        var hooks = CaptureHooks;
        var damageMethod = typeof(Dam_EnemyDamageBase).GetMethod(nameof(Dam_EnemyDamageBase.Setup),
            new[] { typeof(EnemyAgent), typeof(float), typeof(float) })!;
        if (CaptureHarmonyGroup.RejectionReason(damageMethod) == null)
            hooks.Patch(damageMethod,
                prefix: new HarmonyMethod(typeof(LevelGuideCaptureMaterials), nameof(DamageSetupBefore)) { priority = Priority.First, before = plan.BeforeOwners },
                postfix: new HarmonyMethod(typeof(LevelGuideCaptureMaterials), nameof(DamageSetup)) { priority = Priority.First, before = plan.BeforeOwners },
                finalizer: new HarmonyMethod(typeof(LevelGuideCaptureMaterials), nameof(DamageSetupFailed)) { priority = Priority.First, before = plan.BeforeOwners });
        InstallCostCapture(hooks, plan);
        var prefabMethod = typeof(EnemyPrefabManager).GetMethod(nameof(EnemyPrefabManager.BuildEnemyPrefab))!;
        if (CaptureHarmonyGroup.RejectionReason(prefabMethod) == null)
            hooks.Patch(prefabMethod, postfix: new HarmonyMethod(typeof(LevelGuideCaptureMaterials), nameof(PrefabBuilt))
            { priority = Priority.First, before = plan.BeforeOwners });
        else Note("patch-excluded", "EnemyPrefabManager.BuildEnemyPrefab");
        var populationMethod = typeof(EnemyPopulationDataBlock).GetMethod(nameof(EnemyPopulationDataBlock.TryGetEnemyData))!;
        if (CaptureHarmonyGroup.RejectionReason(populationMethod) == null)
            hooks.Patch(populationMethod, postfix: new HarmonyMethod(typeof(LevelGuideCaptureMaterials), nameof(PopulationSelected))
            { priority = Priority.First, before = plan.BeforeOwners });
        var markerMethod = typeof(NavMarkerComponent).GetMethod("Start", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        if (CaptureHarmonyGroup.RejectionReason(markerMethod) == null)
            hooks.Patch(markerMethod, postfix: new HarmonyMethod(typeof(LevelGuideCaptureMaterials), nameof(MarkerStarted))
            { priority = Priority.First, before = plan.BeforeOwners });
        var hackMethod = typeof(Interact_Hack).GetMethod(nameof(Interact_Hack.PlayerCanInteract), new[] { typeof(Player.PlayerAgent) })!;
        if (CaptureHarmonyGroup.RejectionReason(hackMethod) == null)
            hooks.Patch(hackMethod, postfix: new HarmonyMethod(typeof(LevelGuideCaptureMaterials), nameof(HackEligible))
            { priority = Priority.First, before = plan.BeforeOwners });
        foreach (var group in plan.Requests.SelectMany(request =>
            (request.HookComponents.Length == 0 ? new[] { request.Component } : request.HookComponents)
                .Select(component => new { Component = component, Request = request })).GroupBy(row => row.Component))
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(group.Key, false)).FirstOrDefault(value => value != null);
            if (type == null) { Note("missing-component-type", group.Key); continue; }
            if (group.All(row => row.Request.Field == "m_itemModelHolder") && !typeof(Item).IsAssignableFrom(type)) continue;
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var requests = group.Where(row => row.Request.Methods.Contains(method.Name)).Select(row => row.Request).ToArray();
                if (requests.Length == 0 || method.IsGenericMethod || method.Name.StartsWith("get_", StringComparison.Ordinal)
                    || method.Name.StartsWith("set_", StringComparison.Ordinal)) continue;
                try { if (method.GetParameters().Any(parameter => parameter.ParameterType.IsByRef || parameter.ParameterType.IsPointer)) continue; }
                catch (TypeLoadException error) { Note("signature-unavailable", method.Name + ": " + error.Message); continue; }
                var methodKey = LevelGuideCaptureMaterialIdentity.Method(method);
                if (Hooks.TryGetValue(methodKey, out var previous))
                {
                    Hooks[methodKey] = previous.Concat(requests).DistinctBy(request => request.Id).ToArray();
                    continue;
                }
                if (CaptureHarmonyGroup.RejectionReason(method) is { } rejection)
                {
                    Note("patch-excluded", group.Key + "." + method.Name + ": " + rejection);
                    continue;
                }
                try
                {
                    Hooks.Add(methodKey, requests);
                    Plugin.PluginLog.LogDebug("[MaterialHook] installing " + group.Key + "." + method.Name);
                    hooks.Patch(method,
                        prefix: new HarmonyMethod(typeof(LevelGuideCaptureMaterials), nameof(Before)) { priority = Priority.First, before = plan.BeforeOwners },
                        postfix: new HarmonyMethod(typeof(LevelGuideCaptureMaterials), nameof(After)) { priority = Priority.First, before = plan.BeforeOwners });
                }
                catch (Exception error) { Note("patch-unavailable", method + ": " + error.Message); throw; }
            }
        }
    }
    private static void TickInventory(Plan plan)
    {
        if (!_inventoryDone && LevelLanding.HasLanded)
        {
            _inventoryDone = true;
            if (plan.Abilities) { EnemyAbilityInventory.ExportCurrent(); ExportAbilityPrefabs(plan); }
            if (plan.PopulationKeys)
            {
                try
                {
                    var rejected = ExperimentRunner.Run(EnemyPopulationKeyReport.CommandId);
                    _populationStarted = rejected == null;
                    Note(_populationStarted ? "population-start" : "population-unavailable", rejected ?? "Started through the Authoring experiment runner");
                }
                catch (Exception error) { Note("population-unavailable", error.Message); }
            }
        }
        if (_populationStarted && !_populationRecorded && !ExperimentRunner.Running)
        {
            _populationRecorded = true;
            var record = ExperimentRunner.Record(EnemyPopulationKeyReport.CommandId);
            Note("population-result", record.Status + ": " + record.Message);
        }
    }
    private static void Before(object __instance, object?[] __args, MethodBase __originalMethod) => Snapshot(__instance, __args, __originalMethod, "before-native");
    private static void MarkerStarted(NavMarkerComponent __instance, bool __runOriginal)
    { if (CaptureHooks.Installed && __runOriginal) CaptureVanillaContext.MarkerStarted(__instance); }
    private static void After(object __instance, object?[] __args, MethodBase __originalMethod, bool __runOriginal) => Snapshot(__instance, __args, __originalMethod,
        __runOriginal ? "after-native-before-forge" : "original-skipped");
    private sealed class DamageSetupState
    {
        internal EnemyAgent Owner = null!;
        internal long Epoch;
        internal IntPtr Pointer;
        internal uint EnemyId;
        internal string Instance = "", EnemyInstance = "";
        internal object? Configuration;
        internal CaptureVanillaProof.Evidence? EntryProof;
        internal string? PatchOrder;
        internal bool Armed, Completed, ParseMessageHookAbsent;
    }
    // Terminal attribution depends on the reviewed exception formatter and Harmony backend bytes. Unknown builds remain diagnostics.
    private sealed record FailureRuntime(string InteropVersion, string InteropSha256, string HarmonyVersion, string HarmonySha256,
        string HarmonySupportVersion, string HarmonySupportSha256)
    {
        internal bool Reviewed => InteropSha256 == "2ea0e141fb98ab8690822c42fd24a8f9331725247a0011c321cdbde3807b64bc"
            && HarmonySha256 == "64c0107e10f57d34ec2f8097fcb04e3face1b06d6fc77a3df347c6bea06c43dd"
            && HarmonySupportSha256 == "abe4d92c60b4227073a672edc8191b2268447364abcdfd050721ca13dbcc964a";
    }
    private static FailureRuntime? _failureRuntime;
    private static FailureRuntime ReadFailureRuntime()
    {
        static string Hash(Assembly? assembly)
        {
            try { return assembly == null ? "" : ForgeCore.Hash.Sha256Hex(File.ReadAllBytes(assembly.Location)); }
            catch { return ""; } // An unreadable assembly is unreviewed, never a terminal native receipt.
        }
        var support = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => assembly.GetName().Name == "Il2CppInterop.HarmonySupport");
        return _failureRuntime ??= new(typeof(Il2CppException).Assembly.GetName().Version?.ToString() ?? "unknown",
            Hash(typeof(Il2CppException).Assembly), typeof(Harmony).Assembly.GetName().Version?.ToString() ?? "unknown", Hash(typeof(Harmony).Assembly),
            support?.GetName().Version?.ToString() ?? "unknown", Hash(support));
    }
    private static bool NativeIlManipulatorsAbsent(MethodBase? method)
    {
        var flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var groups = new[] { (typeof(EnemyPrefabManager), "BuildEnemyPrefab"), (typeof(EnemyAgent), "Setup"),
            (typeof(Dam_EnemyDamageBase), "Setup"), (typeof(EnemySync), "OnSpawn") }
            .Select(entry => entry.Item1.GetMethods(flags).Where(target => target.Name == entry.Item2).ToArray()).ToArray();
        if (method == null || groups.Any(group => group.Length == 0)) return false;
        var methods = groups.SelectMany(group => group).Cast<MethodBase>().Append(method);
        return methods.Distinct().All(target => Harmony.GetPatchInfo(target)?.ILManipulators.Count is null or 0);
    }
    private static string? NativeSetupPatchOrder(MethodBase method)
    {
        var info = Harmony.GetPatchInfo(method);
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        var before = typeof(LevelGuideCaptureMaterials).GetMethod(nameof(DamageSetupBefore), flags)!;
        var after = typeof(LevelGuideCaptureMaterials).GetMethod(nameof(DamageSetup), flags)!;
        var finalizer = typeof(LevelGuideCaptureMaterials).GetMethod(nameof(DamageSetupFailed), flags)!;
        var manifest = DevelopmentNativeHooks.Shared.TryGetManifest(method);
        if (!CaptureHookProvenance.ReadOnly(manifest) || !CaptureHookProvenance.ExactConsumer(manifest!,
            "NAinfini.ForgeDevelopment.MaterialCapture", DevelopmentNativeHooks.Lane.EarlyPostfix,
            new DevelopmentHookCallbacks.Callback(DevelopmentHookCallbacks.Phase.Prefix, before), new DevelopmentHookCallbacks.Callback(DevelopmentHookCallbacks.Phase.Postfix, after),
            new DevelopmentHookCallbacks.Callback(DevelopmentHookCallbacks.Phase.Finalizer, finalizer))) return null;
        if (!NativeIlManipulatorsAbsent(method) || info == null || info.Prefixes.Count != 1
            || !CaptureHookProvenance.Physical(info.Prefixes[0], manifest, "prefix")
            || info.Finalizers.Count != 1 || !CaptureHookProvenance.Physical(info.Finalizers[0], manifest, "finalizer")
            || info.Transpilers.Count != 0 || info.ILManipulators.Count != 0) return null;
        var compositeAfter = manifest!.Patches.Postfix;
        if (!_plan!.BeforeOwners.All(owner => manifest.Patches.Order.PostfixBefore.Contains(owner))) return null;
        // Sorting a dynamic patch factory would execute it again. Only plain void postfixes qualify here.
        if (info.Postfixes.Any(patch => patch.PatchMethod?.ReturnType != typeof(void)
            || patch.PatchMethod != compositeAfter && !_plan!.BeforeOwners.Contains(patch.owner))) return null;
        var sorted = PatchProcessor.GetSortedPatchMethods(method, info.Postfixes.ToArray());
        if (sorted.Count == 0 || sorted[0] != compositeAfter) return null;
        var fingerprint = string.Join(";", info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers).Select(patch =>
            patch.owner + "/" + patch.PatchMethod.Module.ModuleVersionId + "/" + patch.PatchMethod.MetadataToken + "/"
            + patch.priority + "/" + string.Join(",", patch.before) + "/" + string.Join(",", patch.after)))
            + ";sorted=" + string.Join(",", sorted.Select(patch => patch.Module.ModuleVersionId + "/" + patch.MetadataToken))
            + ";consumers=" + CaptureHookProvenance.Fingerprint(manifest);
        return ForgeCore.Hash.Sha256Hex(fingerprint);
    }
    private static void DamageSetupBefore(Dam_EnemyDamageBase __instance, EnemyAgent __0,
        MethodBase __originalMethod, out DamageSetupState? __state)
    {
        __state = null;
        if (!CaptureHooks.Installed || ReferenceEquals(__0, null) || !RecObservation.TryEnter()) return;
        try
        {
            var data = __0.EnemyData;
            if (data == null || __0.EnemyDataID == 0 || data.persistentID != __0.EnemyDataID) return;
            __state = new DamageSetupState
            {
                Owner = __0,
                Epoch = RuntimeDiagnostics.WorldEpoch,
                Pointer = __instance.Pointer,
                EnemyId = __0.EnemyDataID,
                Instance = RecReflect.Describe(__instance),
                EnemyInstance = RecReflect.Describe(__0),
                Configuration = new { enemyDataId = data.persistentID, balancingDataId = data.BalancingDataId, modelFile = __0.MainModelData?.ModelFile },
                EntryProof = CaptureVanillaProof.Read(__originalMethod, _plan!.BeforeOwners),
                PatchOrder = NativeSetupPatchOrder(__originalMethod),
                ParseMessageHookAbsent = Il2CppException.ParseMessageHook == null
            };
            // Last operation: exceptions caused by observer getters must never look like entry into native Setup.
            __state.Armed = true;
        }
        catch (Exception error) { Note("damage-setup-prefix-read-failed", error.GetType().Name + ": " + error.Message); }
        finally { RecObservation.Exit(); }
    }
    private static Exception? DamageSetupFailed(Dam_EnemyDamageBase __instance, MethodBase __originalMethod,
        bool __runOriginal, Exception? __exception, DamageSetupState? __state)
    {
        if (!CaptureHooks.Installed || __exception == null || __state == null || !RecObservation.TryEnter()) return __exception;
        try
        {
            var proof = CaptureVanillaProof.Read(__originalMethod, _plan!.BeforeOwners);
            var initialized = __instance.IsSetup;
            var runtime = ReadFailureRuntime();
            var nativeStack = __exception.GetType() == typeof(Il2CppException) ? VanillaValueReading.NativeSetupStack(__exception.Message) : null;
            var nativeNull = __exception.Message.StartsWith("NullReferenceException:", StringComparison.Ordinal)
                || __exception.Message.StartsWith("System.NullReferenceException:", StringComparison.Ordinal);
            var patchOrder = NativeSetupPatchOrder(__originalMethod);
            var stableOrder = __state.PatchOrder != null && patchOrder == __state.PatchOrder;
            var parserUnchanged = __state.ParseMessageHookAbsent && Il2CppException.ParseMessageHook == null;
            var leaseCurrent = __state.Epoch == RuntimeDiagnostics.WorldEpoch && __instance.Pointer == __state.Pointer
                && __state.Owner != null && __state.Owner.EnemyDataID == __state.EnemyId;
            var qualified = new VanillaValueReading.SetupFailureChecks(__state.Armed, __runOriginal, __state.Completed, initialized,
                nativeStack != null && nativeNull, parserUnchanged, stableOrder, leaseCurrent, runtime.Reviewed,
                __state.EntryProof?.Qualified == true, proof.Qualified, GameIdentity.GameBuild == _plan.GameBuild).Qualified;
            foreach (var request in _plan.Requests.Where(request => request.Component == "Dam_EnemyDamageLimb"
                && request.Subjects.Any(subject => subject.Type == "Enemy" && subject.Id == __state.EnemyId)))
            {
                var key = __state.Epoch + "/" + request.Id + "/" + __state.Instance + "/native-setup-failed/" + qualified;
                if (Observed.Count >= 100000 || !Observed.Add(key)) continue;
                RecSession.Write("materials", "vanilla-field", json =>
                {
                    VanillaValueReading.Identity(json, request.Id, request.CatalogComponent.Length == 0 ? request.Component : request.CatalogComponent,
                        request.Field, _plan.BuildId, CellBuildData.GetRevision(), GameIdentity.GameBuild, _plan.SourceSha256,
                        __state.Instance, "Enemy", __state.EnemyId, "native-setup-failed", "native-setup-failed",
                        qualified ? "native-initialization-failed" : "native-setup-exception", qualified, qualified);
                    json.WriteString("captureRunId", _plan.CaptureRunId); json.WriteString("requiredWhen", request.When);
                    json.WriteString("nativeMethod", "Dam_EnemyDamageBase.Setup");
                    json.WriteString("nativeComponent", "Dam_EnemyDamageBase");
                    json.WritePropertyName("forgeProof"); JsonSerializer.Serialize(json, proof, CaptureFiles.Json);
                    json.WritePropertyName("nativeSetup"); JsonSerializer.Serialize(json, new
                    {
                        method = "Dam_EnemyDamageBase.Setup",
                        completed = __state.Completed,
                        initialized,
                        runOriginal = __runOriginal,
                        failureOrigin = qualified ? "il2cpp-native-stack" : "unverified",
                        exception = new
                        {
                            type = __exception.GetType().FullName,
                            nativeType = nativeNull ? "NullReferenceException" : null,
                            message = __exception.Message,
                            stack = __exception.StackTrace,
                            nativeStack
                        },
                        owner = new { dataBlockType = "Enemy", dataBlockId = __state.EnemyId, instance = __state.Instance, enemyInstance = __state.EnemyInstance },
                        configuration = __state.Configuration,
                        lease = new { worldEpoch = __state.Epoch, instance = __state.Instance },
                        entryForgeProof = __state.EntryProof,
                        proof = new
                        {
                            exclusiveNativeEntry = __state.Armed && __runOriginal && stableOrder,
                            postfixNotReached = !__state.Completed,
                            nativeTopFrame = nativeStack == null ? null : "Dam_EnemyDamageBase.Setup",
                            exactExceptionType = __exception.GetType() == typeof(Il2CppException),
                            parseMessageHookAbsent = parserUnchanged,
                            parseMessageHookBeforeAbsent = __state.ParseMessageHookAbsent,
                            parseMessageHookAfterAbsent = Il2CppException.ParseMessageHook == null,
                            noIlManipulators = patchOrder != null,
                            patchOrderStable = stableOrder,
                            patchOrderBefore = __state.PatchOrder,
                            patchOrderAfter = patchOrder,
                            leaseCurrent,
                            runtimeReviewed = runtime.Reviewed
                        },
                        runtime
                    }, CaptureFiles.Json);
                });
            }
        }
        catch (Exception error)
        {
            try { Note("damage-setup-failure-read-failed", error.GetType().Name + ": " + error.Message); }
            catch { /* Even a failed diagnostic sink must leave the original game exception untouched. */ }
        }
        finally { RecObservation.Exit(); }
        // Harmony still receives the exact original exception. Recording never suppresses or replaces game failure.
        return __exception;
    }
    private sealed class CostCaptureState
    {
        internal EnemyAgent Owner = null!;
        internal CostCaptureState? Previous;
        internal long Epoch;
        internal uint EnemyId;
        internal long CallId;
        internal int ThreadId;
        internal long? LookupCallId;
        internal int? LookupThreadId;
        internal bool Ready;
        internal int ModeBefore, EnemyType, Dimension, NativeReads;
        internal int? ModeAfter;
        internal float Before;
        internal float? After;
        internal float? SelectedCost;
        internal bool Allocated, LookupQualified;
        internal bool? SameLease, RunOriginal;
        internal object Evidence => new
        {
            method = "Enemies.EnemyAI.ModeChange",
            lookupMethod = "EnemyCostManager.GetEnemyCost",
            modeBefore = ModeBefore,
            modeAfter = ModeAfter,
            enemyType = EnemyType,
            dimension = Dimension,
            beforeCost = Before,
            afterCost = After,
            selectedCost = SelectedCost,
            nativeReads = NativeReads,
            allocationBranchReached = Allocated,
            lookupQualified = LookupQualified,
            worldEpoch = Epoch,
            sameLease = SameLease,
            runOriginal = RunOriginal,
            callId = CallId,
            threadId = ThreadId,
            lookupCallId = LookupCallId,
            lookupThreadId = LookupThreadId,
            evidence = !Allocated ? "unqualified" : NativeReads == 0 ? "native-field-transition" : "native-lookup",
            lookupStatus = NativeReads == 0 ? "native-lookup-not-observed" : NativeReads != 1 ? "ambiguous-native-lookup"
                : Allocated ? "native-allocation-observed" : "allocation-not-qualified"
        };
    }
    [ThreadStatic] private static CostCaptureState? _costCapture;
    private static long _costCallId;
    private static void InstallCostCapture(CaptureHarmonyGroup hooks, Plan plan)
    {
        if (!plan.Requests.Any(request => request.Component == "Enemies.EnemyAgent" && request.Field == "m_enemyCost")) return;
        var mode = typeof(EnemyAI).GetMethod("ModeChange", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
            null, Type.EmptyTypes, null);
        var cost = typeof(EnemyCostManager).GetMethod(nameof(EnemyCostManager.GetEnemyCost), new[] { typeof(eEnemyType) });
        if (mode == null || CaptureHarmonyGroup.RejectionReason(mode) != null)
        { Note("cost-hook-unavailable", "EnemyAI.ModeChange"); return; }
        try
        {
            if (cost == null || CaptureHarmonyGroup.RejectionReason(cost) != null)
                Note("cost-lookup-hook-unavailable", "Unchanged zero needs an additional native lookup receipt");
            else hooks.Patch(cost, postfix: new HarmonyMethod(typeof(LevelGuideCaptureMaterials), nameof(CostSelected))
            { priority = Priority.First, before = plan.BeforeOwners });
        }
        catch (Exception error) { Note("cost-lookup-hook-unavailable", error.GetType().Name + ": " + error.Message); throw; }
        hooks.Patch(mode,
            prefix: new HarmonyMethod(typeof(LevelGuideCaptureMaterials), nameof(CostBefore)) { priority = Priority.First, before = plan.BeforeOwners },
            postfix: new HarmonyMethod(typeof(LevelGuideCaptureMaterials), nameof(CostAfter)) { priority = Priority.First, before = plan.BeforeOwners },
            finalizer: new HarmonyMethod(typeof(LevelGuideCaptureMaterials), nameof(CostFinished)) { priority = Priority.First, before = plan.BeforeOwners });
    }
    private static void CostBefore(EnemyAI __instance, MethodBase __originalMethod, out CostCaptureState? __state)
    {
        __state = null;
        if (!CaptureHooks.Installed || !RecObservation.TryEnter()) return;
        try
        {
            // Push even an unready nested call so its lookups can never be attributed to its parent's lease.
            __state = new CostCaptureState
            {
                Previous = _costCapture,
                Epoch = RuntimeDiagnostics.WorldEpoch,
                CallId = System.Threading.Interlocked.Increment(ref _costCallId),
                ThreadId = Environment.CurrentManagedThreadId
            };
            _costCapture = __state;
            var owner = __instance.m_enemyAgent;
            if (owner == null || owner.EnemyData == null || owner.EnemyDataID == 0) return;
            __state.Owner = owner; __state.EnemyId = owner.EnemyDataID;
            __state.ModeBefore = Convert.ToInt32(__instance.Mode); __state.EnemyType = Convert.ToInt32(owner.EnemyData.EnemyType);
            __state.Dimension = Convert.ToInt32(owner.m_dimensionIndex); __state.Before = owner.m_enemyCost;
            __state.Ready = true;
            SnapshotRequests(owner, Array.Empty<object?>(), __originalMethod, "before-native",
                _plan!.Requests.Where(request => request.Component == "Enemies.EnemyAgent" && request.Field == "m_enemyCost"), costCapture: __state);
        }
        catch (Exception error) { Note("cost-prefix-read-failed", error.GetType().Name + ": " + error.Message); }
        finally { RecObservation.Exit(); }
    }
    private static void CostSelected(eEnemyType __0, float __result, bool __runOriginal, MethodBase __originalMethod)
    {
        var state = _costCapture;
        if (!CaptureHooks.Installed || state == null || !state.Ready || !__runOriginal || !RecObservation.TryEnter()) return;
        try
        {
            // Observe the game's actual lookup. Never call GetEnemyCost just to manufacture a baseline.
            if ((int)__0 != state.EnemyType) return;
            state.NativeReads++;
            state.LookupCallId = state.CallId; state.LookupThreadId = Environment.CurrentManagedThreadId;
            state.SelectedCost = __result;
            state.LookupQualified = CaptureVanillaProof.Read(__originalMethod, _plan!.BeforeOwners).Qualified && NativeIlManipulatorsAbsent(__originalMethod);
        }
        catch (Exception error) { state.LookupQualified = false; Note("cost-lookup-read-failed", error.GetType().Name + ": " + error.Message); }
        finally { RecObservation.Exit(); }
    }
    private static void CostAfter(EnemyAI __instance, MethodBase __originalMethod, bool __runOriginal, CostCaptureState? __state)
    {
        if (!CaptureHooks.Installed || __state == null || !__state.Ready || !RecObservation.TryEnter()) return;
        try
        {
            var owner = __instance.m_enemyAgent;
            if (owner == null) return;
            __state.ModeAfter = Convert.ToInt32(__instance.Mode);
            __state.After = owner.m_enemyCost;
            var sameLease = ReferenceEquals(_costCapture, __state) && __state.ThreadId == Environment.CurrentManagedThreadId
                && (__state.NativeReads == 0 || __state.LookupCallId == __state.CallId && __state.LookupThreadId == __state.ThreadId)
                && __state.Epoch == RuntimeDiagnostics.WorldEpoch
                && owner.Pointer == __state.Owner.Pointer && owner.EnemyDataID == __state.EnemyId
                && owner.EnemyData != null && Convert.ToInt32(owner.EnemyData.EnemyType) == __state.EnemyType
                && Convert.ToInt32(owner.m_dimensionIndex) == __state.Dimension;
            __state.SameLease = sameLease; __state.RunOriginal = __runOriginal;
            __state.Allocated = VanillaValueReading.CostAllocationObserved(__runOriginal, sameLease, __state.ModeBefore,
                __state.ModeAfter.Value, __state.Before, __state.After.Value, __state.NativeReads, __state.SelectedCost);
            SnapshotRequests(owner, Array.Empty<object?>(), __originalMethod,
                __runOriginal ? "after-native-before-forge" : "original-skipped",
                _plan!.Requests.Where(request => request.Component == "Enemies.EnemyAgent" && request.Field == "m_enemyCost"), costCapture: __state);
        }
        catch (Exception error) { Note("cost-read-failed", error.GetType().Name + ": " + error.Message); }
        finally { RecObservation.Exit(); }
    }
    private static Exception? CostFinished(Exception? __exception, CostCaptureState? __state)
    {
        if (__state != null && ReferenceEquals(_costCapture, __state)) _costCapture = __state.Previous;
        return __exception;
    }
    private static void DamageSetup(Dam_EnemyDamageBase __instance, MethodBase __originalMethod, bool __runOriginal, DamageSetupState? __state)
    {
        // This marker precedes every observer read, even when recording is suppressed.
        if (__state != null) __state.Completed = __runOriginal;
        if (!CaptureHooks.Installed || !__runOriginal || !RecObservation.TryEnter()) return;
        try
        {
            if (__instance.DamageLimbs == null) return;
            foreach (var limb in __instance.DamageLimbs)
                if (limb != null) SnapshotRequests(limb, Array.Empty<object?>(), __originalMethod,
                    "after-native-before-forge", _plan!.Requests.Where(request => request.Component == "Dam_EnemyDamageLimb"));
        }
        catch (Exception error) { Note("damage-setup-read-failed", error.GetType().Name + ": " + error.Message); }
        finally { RecObservation.Exit(); }
    }
    private static void HackEligible(Interact_Hack __instance, bool __result, bool __runOriginal, MethodBase __originalMethod)
    {
        if (!CaptureHooks.Installed || !__runOriginal || !__result || !RecObservation.TryEnter()) return;
        try
        {
            var tool = __instance.GetEquippedHackingTool();
            if (tool?.ItemDataBlock == null) return;
            SnapshotRequests(__instance, Array.Empty<object?>(), __originalMethod, "after-native-before-forge",
                _plan!.Requests.Where(request => request.Component == "Interact_Hack"), tool.ItemDataBlock.persistentID);
        }
        catch (Exception error) { Note("hack-read-failed", error.GetType().Name + ": " + error.Message); }
        finally { RecObservation.Exit(); }
    }
    private static void PrefabBuilt(EnemyDataBlock data, GameObject __result, bool __runOriginal, MethodBase __originalMethod)
    {
        if (!CaptureHooks.Installed || !__runOriginal || __result == null || !RecObservation.TryEnter()) return;
        try
        {
            foreach (var component in ForgeCore.GameFloor.Components(__result.transform))
                if (component != null && !component.WasCollected)
                    SnapshotRequests(component, Array.Empty<object?>(), __originalMethod, "after-native-before-forge", _plan!.Requests, data.persistentID);
        }
        catch (Exception error) { Note("prefab-read-failed", error.GetType().Name + ": " + error.Message); }
        finally { RecObservation.Exit(); }
    }
    private static void PopulationSelected(EnemyPopulationDataBlock __instance, eEnemyRole role,
        eEnemyRoleDifficulty difficulty, EnemyDataBlock enemyData, float cost, bool __result, bool __runOriginal, MethodBase __originalMethod)
    {
        if (!CaptureHooks.Installed || !__runOriginal || !__result || enemyData == null || !RecObservation.TryEnter()) return;
        try
        {
            var candidates = new List<int>();
            for (var index = 0; index < __instance.RoleDatas.Count; index++)
            {
                var row = __instance.RoleDatas[index];
                if (row.Enemy == enemyData.persistentID && row.Role == role && row.Difficulty == difficulty) candidates.Add(index);
            }
            foreach (var request in _plan!.Requests.Where(request => request.Component == "GameData.EnemyPopulationDataBlock"
                && request.Subjects.Any(subject => subject.Type == "Enemy" && subject.Id == enemyData.persistentID)))
            {
                var proof = CaptureVanillaProof.Read(__originalMethod, _plan.BeforeOwners);
                var beforeForge = proof.Qualified && GameIdentity.GameBuild == _plan.GameBuild;
                var status = candidates.Count != 1 ? "selected-entry-unavailable"
                    : beforeForge ? "observed" : "override-order-unverified";
                RecSession.Write("materials", "vanilla-field", json =>
                {
                    VanillaValueReading.Identity(json, request.Id, request.Component, request.Field, _plan.BuildId,
                        CellBuildData.GetRevision(), GameIdentity.GameBuild, _plan.SourceSha256,
                        RecReflect.Describe(__instance), "Enemy", enemyData.persistentID,
                        role + "/" + difficulty, "after-native-before-forge", status, candidates.Count == 1, beforeForge);
                    json.WriteString("captureRunId", _plan.CaptureRunId);
                    json.WriteString("requiredWhen", request.When);
                    json.WritePropertyName("forgeProof"); JsonSerializer.Serialize(json, proof, CaptureFiles.Json);
                    json.WriteBoolean("contextual", true); json.WriteNumber("populationDataBlockId", __instance.persistentID);
                    json.WriteNumber("returnedCost", cost); json.WritePropertyName("candidateIndices"); JsonSerializer.Serialize(json, candidates);
                    if (status != "observed") return;
                    var row = __instance.RoleDatas[candidates[0]];
                    json.WriteNumber("roleDataIndex", candidates[0]); json.WritePropertyName("rawValue");
                    if (request.Field == "RoleDatas") JsonSerializer.Serialize(json, new
                    {
                        enemy = row.Enemy,
                        role = (int)row.Role,
                        difficulty = (int)row.Difficulty,
                        weight = row.Weight,
                        cost = row.Cost
                    });
                    else RecReflect.WriteValue(json, RecReflect.ReadPath(row, request.Field[(request.Field.LastIndexOf('.') + 1)..]), 0);
                });
            }
        }
        catch (Exception error) { Note("population-read-failed", error.GetType().Name + ": " + error.Message); }
        finally { RecObservation.Exit(); }
    }
    private static void Snapshot(object instance, object?[] arguments, MethodBase method, string phase)
    {
        if (!CaptureHooks.Installed || !RecObservation.TryEnter()) return;
        try
        {
            var methodKey = LevelGuideCaptureMaterialIdentity.Method(method);
            if (!Hooks.TryGetValue(methodKey, out var requests))
            {
                if (UnresolvedHooks.Add(methodKey)) Note("hook-identity-unresolved", method.DeclaringType?.FullName + "." + method.Name);
                return;
            }
            SnapshotRequests(instance, arguments, method, phase, requests);
        }
        catch (Exception error) { Note("snapshot-unavailable", error.GetType().Name + ": " + error.Message); }
        finally { RecObservation.Exit(); }
    }
    private static bool Matches(object instance, string component)
    {
        for (var native = RecReflect.Probe.NativeClass(instance); native != IntPtr.Zero; native = IL2CPP.il2cpp_class_get_parent(native))
            if (RecReflect.Probe.NativeClassName(native) == component) return true;
        return false;
    }
    private static void SnapshotRequests(object instance, object?[] arguments, MethodBase? method, string phase, IEnumerable<Request> requests, uint? prefabEnemyId = null, CostCaptureState? costCapture = null)
    {
        // GetComponents returns Component wrappers. C# `is EnemyAgent` does not inspect the IL2CPP class.
        if (instance is Il2CppObjectBase native)
        {
            if (Matches(instance, "Enemies.EnemyAgent")) instance = native.TryCast<EnemyAgent>()!;
            else if (Matches(instance, "Dam_EnemyDamageLimb")) instance = native.TryCast<Dam_EnemyDamageLimb>()!;
            else if (Matches(instance, "Item")) instance = native.TryCast<Item>()!;
        }
        var proof = CaptureVanillaProof.Read(method, _plan!.BeforeOwners);
        foreach (var request in requests)
        {
            if (!Matches(instance, request.Component)) continue;
            var modelRequest = request.Field.StartsWith("MainModelTransform.", StringComparison.Ordinal)
                || request.Field.StartsWith("MaterialHandler.", StringComparison.Ordinal);
            var reached = CaptureVanillaContext.PhaseReached(instance, request.Field, request.When, method, phase, costCapture?.Allocated == true);
            // Prefab assembly precedes AI locomotion. Scanner state cannot describe model initialization.
            var state = modelRequest ? VanillaValueReading.ModelState(reached)
                : instance is EnemyAgent agent ? CaptureVanillaContext.ScannerState(agent) + "/" + (agent.AI?.m_locomotion?.CurrentStateEnum.ToString() ?? "native-setup")
                : RecReflect.ReadPath(instance, "m_stateEnum")?.ToString() ?? method?.Name ?? "landing";
            var key = VanillaValueReading.InvocationKey(RuntimeDiagnostics.WorldEpoch + "/" + request.Id + "/" + RecReflect.Describe(instance)
                + "/" + method?.Name + "/" + phase + "/" + state, costCapture?.CallId);
            var dataBlock = request.DataBlockPaths.Select(path => new { path, value = RecReflect.ReadPath(instance, path) }).FirstOrDefault(row => row.value != null);
            var itemPrefix = request.KeyType == "item-or-gear"
                ? dataBlock?.path?.Replace("pItemData.itemID_gearCRC", "", StringComparison.Ordinal) ?? "" : null;
            var itemDataPath = itemPrefix == null ? null : itemPrefix + "<ItemDataBlock>k__BackingField.<persistentID>k__BackingField";
            var itemDataId = itemDataPath == null ? null : RecReflect.ReadPath(instance, itemDataPath);
            if (itemDataId == null && instance is Component carrier)
                itemDataId = carrier.GetComponentInParent<Item>()?.ItemDataBlock?.persistentID;
            var nativeId = prefabEnemyId ?? CaptureVanillaContext.NativeId(request.KeyType == "item-or-gear" ? itemDataId : dataBlock?.value);
            var subject = request.Subjects.FirstOrDefault(row => row.Id == nativeId);
            if (request.Subjects.Length > 0 && subject == null && nativeId.HasValue) continue;
            // Filter the DataBlock first: unrelated enemy requests must not consume the capture budget.
            if (Observed.Count >= 100000)
            {
                if (!_recordBudgetReported) { _recordBudgetReported = true; Note("record-budget-exhausted", "100000 distinct reading contexts; remaining values are unobserved"); }
                continue;
            }
            if (!Observed.Add(key)) continue;
            var reading = CaptureVanillaContext.ReadOutcome(instance, request.Field, request.Members, request.When);
            var value = reading.Value;
            var beforeForge = phase == "after-native-before-forge" && method != null && proof.Qualified && NativeIlManipulatorsAbsent(method)
                && GameIdentity.GameBuild == _plan!.GameBuild;
            var status = VanillaValueReading.Status(phase, beforeForge, reached, reading.Available);
            if (status == "observed" && !nativeId.HasValue) status = "missing-datablock-context";
            if (status == "observed" && reading.Outcome == "explicit-null" && !request.Field.StartsWith("MaterialHandler.", StringComparison.Ordinal))
                status = "field-unavailable";
            if (status == "observed" && request.Field == "m_enemyCost" && costCapture is { NativeReads: > 0, LookupQualified: false })
                status = "override-order-unverified";
            string? targetPrefab = null;
            if (request.KeyType == "hack-target" && instance is Component component)
            {
                Component? target = component.GetComponentInParent<LG_WeakResourceContainer>();
                target ??= component.GetComponentInParent<LG_WeakDoor>();
                target ??= component.GetComponentInParent<LG_WeakLock>();
                if (target != null) targetPrefab = GameIdentity.NativePrefabName(target.gameObject.name);
            }
            RecSession.Write("materials", "vanilla-field", json =>
            {
                VanillaValueReading.Identity(json, request.Id,
                    request.CatalogComponent.Length == 0 ? request.Component : request.CatalogComponent, request.Field,
                    _plan!.BuildId, CellBuildData.GetRevision(), GameIdentity.GameBuild, _plan.SourceSha256,
                    RecReflect.Describe(instance), subject?.Type ?? "unknown", nativeId, state, phase, status,
                    reached, beforeForge);
                json.WriteString("captureRunId", _plan.CaptureRunId);
                json.WritePropertyName("forgeProof"); JsonSerializer.Serialize(json, proof, CaptureFiles.Json);
                json.WriteString("nativeMethod", method?.DeclaringType?.FullName + "." + method?.Name);
                if (instance is Dam_EnemyDamageLimb contextLimb)
                {
                    json.WritePropertyName("nativeSetup"); JsonSerializer.Serialize(json, new
                    {
                        method = method?.DeclaringType?.FullName + "." + method?.Name,
                        completed = phase == "after-native-before-forge" && method?.DeclaringType == typeof(Dam_EnemyDamageBase),
                        initialized = CaptureVanillaContext.LimbInitialized(contextLimb)
                    });
                }
                json.WriteString("requiredWhen", request.When);
                json.WritePropertyName("memberRead"); JsonSerializer.Serialize(json, reading.Evidence);
                if (instance is EnemyAgent modelEnemy && modelRequest)
                {
                    json.WritePropertyName("nativeInitialization"); JsonSerializer.Serialize(json, new
                    {
                        method = method?.DeclaringType?.FullName + "." + method?.Name,
                        completed = phase == "after-native-before-forge" && method?.DeclaringType == typeof(EnemyPrefabManager)
                            && method.Name == "BuildEnemyPrefab",
                        initialized = CaptureVanillaContext.ModelInitialized(modelEnemy),
                        modelFile = modelEnemy.MainModelData?.ModelFile,
                        modelInstance = modelEnemy.MainModelTransform?.GetInstanceID()
                    });
                }
                if (costCapture != null)
                {
                    json.WritePropertyName("costAllocation"); JsonSerializer.Serialize(json, costCapture.Evidence);
                }
                if (status == "observed" && reading.SelectedBlock != null)
                {
                    json.WritePropertyName("selectedBlock"); JsonSerializer.Serialize(json, reading.SelectedBlock);
                }
                json.WriteString("nativeComponent", RecReflect.Probe.NativeClassName(RecReflect.Probe.NativeClass(instance)));
                json.WriteString("method", method?.Name); json.WriteNumber("frame", Time.frameCount);
                json.WriteBoolean("contextual", CaptureVanillaContext.Contextual(request.Field, request.When));
                json.WritePropertyName("stateContext"); RecReflect.WriteValue(json,
                    instance is EnemyAgent contextEnemy ? new
                    {
                        locomotion = contextEnemy.AI?.m_locomotion?.CurrentStateEnum.ToString(),
                        behavior = contextEnemy.AI?.m_behaviour?.m_currentStateName.ToString(),
                        soundIndex = contextEnemy.m_scannerData?.m_soundIndex
                    }
                    : new
                    {
                        state = RecReflect.ReadPath(instance, "m_stateEnum"),
                        detection = RecReflect.ReadPath(instance, "m_detectData"),
                        heartbeat = RecReflect.ReadPath(instance, "m_heartbeatActive"),
                        closestDistanceColor = RecReflect.ReadPath(instance, "s_closestDistanceDetectionColorVec")
                    } as object, 0);
                json.WriteString("dataBlockPath", itemDataPath ?? dataBlock?.path);
                json.WriteString("keyType", request.KeyType);
                json.WritePropertyName("keyId"); RecReflect.WriteValue(json, dataBlock?.value, 0);
                json.WriteString("targetPrefab", targetPrefab);
                if (request.KeyType == "item-or-gear")
                {
                    json.WritePropertyName("itemDataId"); RecReflect.WriteValue(json, itemDataId, 0);
                }
                if (request.KeyType == "item-or-gear")
                {
                    json.WritePropertyName("inventorySlot");
                    RecReflect.WriteValue(json, RecReflect.ReadPath(instance,
                        (dataBlock?.path?.Replace("itemID_gearCRC", "slot", StringComparison.Ordinal)) ?? "pItemData.slot"), 0);
                }
                json.WritePropertyName("arguments"); json.WriteStartObject();
                var parameters = method?.GetParameters() ?? Array.Empty<ParameterInfo>();
                for (var index = 0; index < arguments.Length; index++)
                {
                    json.WritePropertyName(index < parameters.Length ? parameters[index].Name ?? index.ToString() : index.ToString());
                    RecReflect.WriteValue(json, arguments[index], 0);
                }
                json.WriteEndObject();
                json.WritePropertyName("value"); CaptureVanillaContext.WriteRaw(json, value);
                if (status == "observed") { json.WritePropertyName("rawValue"); CaptureVanillaContext.WriteRaw(json, value); }
            });
        }
    }
    private static void ExportAbilityPrefabs(Plan plan)
    {
        var assets = AssetShards.AssetShardManager.s_loadedAssetsLookup;
        if (assets == null) { Note("ability-assets-unavailable", "Native loaded asset lookup is absent"); return; }
        var requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var enemy in GameDataBlockBase<EnemyDataBlock>.GetAllBlocks())
            foreach (var ability in enemy.AI_Abilities) requested.Add(ability.AbilityPrefab);
        foreach (var asset in assets)
        {
            if (!requested.Remove(asset.Key) || asset.Value?.TryCast<GameObject>() is not { } prefab) continue;
            Plugin.PluginLog.LogDebug("[MaterialRead] ability prefab " + asset.Key);
            foreach (var component in ForgeCore.GameFloor.Components(prefab.transform))
            {
                if (component == null || component.WasCollected) continue;
                var nativeType = RecReflect.Probe.NativeClassName(RecReflect.Probe.NativeClass(component)) ?? component.GetType().FullName ?? "unknown";
                var shortName = nativeType[(nativeType.LastIndexOf('.') + 1)..];
                if (!plan.NativeFields.TryGetValue(shortName, out var fields)) continue;
                RecSession.Write("materials", "ability-prefab", json =>
                {
                    json.WriteString("prefab", asset.Key); json.WriteString("component", nativeType);
                    json.WriteString("phase", "loaded-native-prefab-template"); json.WriteNumber("gameRevision", CellBuildData.GetRevision());
                    json.WritePropertyName("fields"); json.WriteStartObject();
                    foreach (var field in fields)
                    {
                        Plugin.PluginLog.LogDebug("[MaterialRead] " + asset.Key + " " + nativeType + "." + field);
                        json.WritePropertyName(field); RecReflect.WriteValue(json, RecReflect.ReadPath(component, field), 0);
                    }
                    json.WriteEndObject();
                });
            }
        }
        foreach (var path in requested) Note("ability-prefab-not-loaded", path);
    }
    private static void Note(string status, string detail)
    {
        try
        {
            RecSession.Write("materials", "status", json =>
            { json.WriteString("status", status); json.WriteString("detail", detail); });
        }
        catch { /* A failed diagnostic sink must not interrupt native gameplay or replace its exception. */ }
    }
}
