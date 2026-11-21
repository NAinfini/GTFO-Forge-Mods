using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using ChainedPuzzles;
using Il2CppInterop.Runtime.InteropTypes;
using LevelGeneration;
using UnityEngine;

namespace ForgeDevelopment.Native;

/// <summary>Return-boundary observations of the exact captured instances. Never stops a coroutine, removes a
/// delegate or changes native cleanup. Unity destruction and IL2CPP collection are recorded separately.</summary>
internal sealed class ExitScanLifetimeCapture
{
    private sealed record Core(Component Component, long Pointer, CP_PlayerScanner? Scanner, long ScannerPointer, string Path);
    private sealed record Puzzle(string Source, ChainedPuzzleInstance Instance, long Pointer, string Uid,
        string Path, long Parent, long CallbackTarget, string CallbackTargets, int CallbackCount, Core[] Cores);
    private readonly long _world;
    private readonly long _floorAtCapture;
    private readonly List<Puzzle> _puzzles = new();

    internal ExitScanLifetimeCapture(long world) { _world = world; _floorAtCapture = Pointer(Builder.CurrentFloor); }

    internal void Add(string source, ChainedPuzzleInstance instance)
    {
        if (_puzzles.Exists(puzzle => puzzle.Pointer == instance.Pointer.ToInt64())) return;
        if (_puzzles.Count == 64) throw new InvalidOperationException("exit-scan.lifetime-puzzle-budget");
        var cores = new List<Core>();
        var native = instance.m_chainedPuzzleCores;
        if (native == null) throw new InvalidOperationException("exit-scan.lifetime-cores-unavailable: " + source);
        if (native.Length > 128) throw new InvalidOperationException("exit-scan.lifetime-core-budget: " + source);
        foreach (var value in native)
        {
            var component = value?.TryCast<Component>();
            if (component == null || component.WasCollected)
                throw new InvalidOperationException("exit-scan.lifetime-core-unavailable: " + source);
            var bioscan = component.TryCast<CP_Bioscan_Core>();
            var scanner = bioscan?.m_playerScanner?.TryCast<CP_PlayerScanner>();
            cores.Add(new(component, component.Pointer.ToInt64(), scanner, Pointer(scanner), ExitScanEnvironment.PathOf(component.transform)));
        }
        var callback = instance.Master_OnPlayerScanChangedCallback;
        var callbacks = callback?.GetInvocationList();
        if (callbacks?.Length > 64) throw new InvalidOperationException("exit-scan.lifetime-callback-budget: " + source);
        var targets = new List<string>();
        if (callbacks != null) foreach (var invocation in callbacks) targets.Add(Hex(invocation.Target?.Pointer.ToInt64() ?? 0));
        _puzzles.Add(new(source, instance, instance.Pointer.ToInt64(), instance.m_puzzleUID,
            ExitScanEnvironment.PathOf(instance.transform), Pointer(instance.transform.parent),
            callback?.Target?.Pointer.ToInt64() ?? 0, string.Join(",", targets), callbacks?.Length ?? 0, cores.ToArray()));
    }

    internal void Observe(string phase)
    {
        var manager = ChainedPuzzleManager.Current;
        var floor = Builder.CurrentFloor;
        var landing = ElevatorShaftLanding.Current;
        foreach (var puzzle in _puzzles)
        {
            int references = 0;
            if (manager != null && !manager.WasCollected && manager.m_instances != null)
                foreach (var current in manager.m_instances)
                    if (current is not null && !current.WasCollected && current.Pointer.ToInt64() == puzzle.Pointer) references++;
            var fields = new Dictionary<string, string>
            {
                ["phase"] = phase,
                ["capturedWorld"] = Number(_world),
                ["currentWorld"] = Number(ExitScanEnvironment.WorldEpoch),
                ["captureFloor"] = Hex(_floorAtCapture),
                ["frame"] = Number(ExitScanEnvironment.Frame),
                ["source"] = puzzle.Source,
                ["instance"] = Hex(puzzle.Pointer),
                ["uid"] = puzzle.Uid,
                ["instancePath"] = puzzle.Path,
                ["capturedParent"] = Hex(puzzle.Parent),
                ["managedCollected"] = puzzle.Instance.WasCollected.ToString(),
                ["unityAlive"] = Alive(puzzle.Instance).ToString(),
                ["managerReferences"] = Number(references),
                ["instanceActive"] = Alive(puzzle.Instance) ? puzzle.Instance.IsActive.ToString() : "unavailable",
                ["currentFloor"] = Hex(Pointer(floor)),
                ["capturedCallbackLastTarget"] = Hex(puzzle.CallbackTarget),
                ["currentLanding"] = Hex(Pointer(landing)),
                ["landingUnityAlive"] = (landing != null && !landing.WasCollected).ToString(),
                ["capturedCallbackTargets"] = puzzle.CallbackTargets,
                ["capturedCallbackCount"] = Number(puzzle.CallbackCount),
                ["cores"] = Number(puzzle.Cores.Length)
            };
            Write(fields);
            foreach (var core in puzzle.Cores)
            {
                var scanner = core.Scanner;
                var scannerAlive = scanner != null && !scanner.WasCollected;
                var coreFields = new Dictionary<string, string>
                {
                    ["phase"] = phase,
                    ["capturedWorld"] = Number(_world),
                    ["currentWorld"] = Number(ExitScanEnvironment.WorldEpoch),
                    ["frame"] = Number(ExitScanEnvironment.Frame),
                    ["instance"] = Hex(puzzle.Pointer),
                    ["core"] = Hex(core.Pointer),
                    ["capturedCorePath"] = core.Path,
                    ["coreUnityAlive"] = Alive(core.Component).ToString(),
                    ["scanner"] = Hex(core.ScannerPointer),
                    ["scannerUnityAlive"] = scannerAlive.ToString(),
                    ["scannerRead"] = scanner is null ? "unsupported-core" : scannerAlive ? "read" : "destroyed-or-collected"
                };
                if (scannerAlive)
                {
                    coreFields["scanActive"] = scanner!.m_scanActive.ToString();
                    coreFields["scanRoutine"] = Hex(Pointer(scanner.m_scanRoutine));
                    coreFields["closeRoutine"] = Hex(Pointer(scanner.m_checkPlayerIsCloseRoutine));
                    coreFields["lastPlayerCount"] = Number(scanner.m_lastPlayerCount);
                    coreFields["scannerCallbackTarget"] = Hex(scanner.OnScanStateChanged?.Target?.Pointer.ToInt64() ?? 0);
                }
                Write(coreFields);
            }
        }
    }

    private static bool Alive(UnityEngine.Object value) => !value.WasCollected && value != null;
    private static long Pointer(Il2CppObjectBase? value) => value is null || value.WasCollected ? 0 : value.Pointer.ToInt64();
    private static string Hex(long value) => "0x" + value.ToString("x", CultureInfo.InvariantCulture);
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static void Write(Dictionary<string, string> fields)
    {
        ExitScanSettle.LifetimeSink?.Invoke(fields);
        ExitScanEnvironment.Info("exit-scan-lifetime " + JsonSerializer.Serialize(fields));
    }
}
