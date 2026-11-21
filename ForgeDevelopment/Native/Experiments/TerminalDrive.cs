using System;
using System.Collections.Generic;
using LevelGeneration;
using Player;
using UnityEngine;

namespace ForgeDevelopment.Native;

/// <summary>The console's terminal commands, for the terminal the local player is using (entered with <c>interact</c>). Each goes
/// through the game's own entry for the key a player presses: Enter is the interaction state's <c>OnReturn</c> on the line the player
/// typed (<c>LG_ComputerTerminal.m_currentLine</c>), so the command runs, is synced and prints exactly as typed by hand; leaving is the
/// terminal's own <c>ExitFPSView</c>. Every answer is one <c>name=value; ...</c> line.</summary>
internal static class TerminalDrive
{
    private const int MaxLine = 200, MaxTail = 40;

    /// <summary>Types <paramref name="line"/> into the terminal in use and presses Enter.</summary>
    internal static string Type(string line)
    {
        if (line.Length == 0 || line.Length > MaxLine) throw new ArgumentException("terminal.line-length: 1.." + MaxLine + " characters");
        var terminal = InUse();
        var state = terminal.m_currentState?.TryCast<LG_TERM_PlayerInteracting>()
            ?? throw new InvalidOperationException("terminal.not-taking-input: state " + terminal.CurrentStateName);
        terminal.m_currentLine = line;
        terminal.m_caretBlinkOffsetFromEnd = 0;
        state.OnReturn();
        return "typed=" + Clean(line) + "; terminal=" + terminal.m_serialNumber + "; state=" + terminal.CurrentStateName;
    }

    /// <summary>Leaves the terminal in use the way the game's own exit key does.</summary>
    internal static string Exit()
    {
        var terminal = InUse();
        terminal.ExitFPSView();
        return "exited=" + terminal.m_serialNumber;
    }

    /// <summary>The last <paramref name="count"/> lines of the terminal screen (ForgeCore's screen buffer read) and whether the local
    /// player is still using it. Without a terminal in use it reads the nearest one within 3 m of the player.</summary>
    internal static string Read(int count)
    {
        var player = PlayerActions.Local;
        var terminal = Using(player) ?? Nearest(player.Position, 3f)
            ?? throw new InvalidOperationException("terminal.none: no terminal in use or within 3 m");
        var lines = new List<string>();
        ForgeCore.WorldStates.TerminalLines(terminal, lines);
        count = Math.Clamp(count, 1, MaxTail);
        var tail = lines.GetRange(Math.Max(0, lines.Count - count), Math.Min(count, lines.Count));
        var using_ = Using(player);
        return "terminal=" + terminal.m_serialNumber + "; using=" + (using_ != null && using_.Pointer == terminal.Pointer).ToString().ToLowerInvariant()
            + "; state=" + terminal.CurrentStateName + "; lines=" + lines.Count + "; tail=" + string.Join(" | ", tail.ConvertAll(Clean));
    }

    private static LG_ComputerTerminal InUse()
        => Using(PlayerActions.Local) ?? throw new InvalidOperationException("terminal.not-in-use: interact with a terminal first");

    private static LG_ComputerTerminal? Using(PlayerAgent player)
    {
        foreach (var terminal in UnityEngine.Object.FindObjectsOfType<LG_ComputerTerminal>())
            if (terminal != null && !terminal.WasCollected && terminal.m_localInteractionSource != null
                && terminal.m_localInteractionSource.Pointer == player.Pointer)
                return terminal;
        return null;
    }

    private static LG_ComputerTerminal? Nearest(Vector3 at, float radius)
    {
        LG_ComputerTerminal? best = null;
        foreach (var terminal in UnityEngine.Object.FindObjectsOfType<LG_ComputerTerminal>())
        {
            if (terminal == null || terminal.WasCollected) continue;
            var distance = Vector3.Distance(at, terminal.transform.position);
            if (distance <= radius) { best = terminal; radius = distance; }
        }
        return best;
    }

    // The console splits answers on "; " and "=", and a terminal line carries rich-text tags and line breaks.
    private static string Clean(string text)
        => System.Text.RegularExpressions.Regex.Replace(text ?? "", "<[^>]*>", "").Replace("\n", " ").Replace("\r", " ").Replace(";", ",").Trim();
}
