using System;
using System.Collections.Generic;
using BepInEx;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;

namespace ForgeMap.Native;

/// <summary>
/// The wave anchors this install authored, read once at plugin start from the package documents that declare
/// them: `plugins/&lt;package&gt;/forge/wave-anchors.forge`, found by the same one-level package scan every other
/// authored content file of this module uses.
///
/// The document is the author's own anchor table, so it is read strictly: a row that does not read is refused
/// with its own diagnostic and left out, and a settings id claimed twice for one level is refused the second time
/// rather than applied twice — the id is what a wave is read back with, so two rows sharing one would anchor a
/// wave nobody wrote. The snapshot is immutable once loaded, like the fixed placements and for the same reason:
/// two machines disagreeing about where a wave spawns would be a different world on each of them.
///
/// The half itself holds no native object: it answers the one question the wave hook asks — which anchor this
/// settings id resolves to in this world — through the per-epoch table, which is built from the level the game
/// generated. It is the one installed half, so a hook reads it through <see cref="Current"/>; a half that was
/// never started answers nothing, and the wave runs exactly as the author's data blocks wrote it.
/// </summary>
internal sealed class WaveAnchorSession : IDisposable
{
    internal const string WorldUnavailable = "wave-anchor-world-unavailable";
    private static readonly PublishedTextCodes ReadCodes = new("wave-anchor-path", "wave-anchor-unreadable", "wave-anchor-file-size",
        "wave-anchor-json", "wave-anchor-utf8");
    private IReadOnlyList<WaveAnchor> _anchors;
    private readonly Func<long> _worldEpoch;
    private readonly Func<MapLevelReference?> _level;
    private readonly Action<string> _report;
    private bool _disposed;

    private WaveAnchorSession(IReadOnlyList<WaveAnchor> anchors, Func<long> worldEpoch, Func<MapLevelReference?> level,
        Action<string> report)
    {
        _anchors = anchors;
        _worldEpoch = worldEpoch;
        _level = level;
        _report = report;
    }

    /// <summary>The one installed half. A Harmony patch is a static method, so the session hands itself to the
    /// hook through this slot when it starts; a hook that fires before any session exists leaves the wave alone
    /// rather than throwing through a patch.</summary>
    internal static WaveAnchorSession? Current { get; private set; }

    /// <summary>Reads this install's anchor documents and answers the session that resolves them. A document that
    /// does not read is reported and left out; it never stops the package from loading. The session installs
    /// itself as the current half, because the hook it answers reads only that slot.</summary>
    internal static WaveAnchorSession Start(Func<long> worldEpoch, Func<MapLevelReference?> level, Action<string> report,
        IReadOnlyList<WaveAnchor>? prepared = null)
    {
        ArgumentNullException.ThrowIfNull(worldEpoch);
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(report);
        var anchors = prepared ?? Load(Paths.BepInExRootPath, report);
        var session = new WaveAnchorSession(anchors, worldEpoch, level, report);
        Current = session;
        return session;
    }

    /// <summary>Switches the declaration table without resolving rooms or touching native wave state.</summary>
    internal void AdmitRows(IReadOnlyList<WaveAnchor> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _anchors = rows;
        WaveAnchorTable.Reset();
    }

    /// <summary>How many anchors this install's documents gave, for the plugin's one load record.</summary>
    internal int Anchors => _anchors.Count;

    /// <summary>Drops the half when it is still the current one, so a session that is going away cannot leave its
    /// table in the slot a later session's waves would resolve through.</summary>
    internal static void Detach(WaveAnchorSession session)
    {
        if (ReferenceEquals(Current, session)) Current = null;
    }

    /// <summary>
    /// The anchor one settings id resolves to in this world, or the one reason it cannot be resolved. Three
    /// answers are told apart, and the caller needs all three: a resolved anchor, a refusal of a declared anchor,
    /// and "no anchor claims this id" — the last one is the leave-alone case, where the wave runs exactly as the
    /// author's data blocks wrote it. A declared id whose world or level identity is temporarily unreadable is a
    /// refusal: allowing it through would silently replace the authored anchor with the caller's location.
    /// </summary>
    internal bool TryResolve(uint settingsId, out WaveAnchorTarget? target, out string? refusal)
    {
        target = null;
        refusal = null;
        if (_disposed || _anchors.Count == 0) return false;
        var declared = false;
        for (var index = 0; index < _anchors.Count; index++)
            if (_anchors[index].SettingsId == settingsId) { declared = true; break; }
        if (!declared) return false;
        var epoch = _worldEpoch();
        if (epoch <= 0 || _level() is not { } level)
        { refusal = WorldUnavailable; return false; }
        return WaveAnchorTable.Current(epoch, _anchors, level, _report).TryResolve(settingsId, out target, out refusal);
    }

    /// <summary>Every anchor every package of this install declares, in ordinal document order and, inside one
    /// document, in the order the author wrote them. A settings id claimed twice for one level is refused the
    /// second time, across documents as well as inside one.</summary>
    private static IReadOnlyList<WaveAnchor> Load(string bepInExRoot, Action<string> rejected)
    {
        ArgumentNullException.ThrowIfNull(bepInExRoot);
        ArgumentNullException.ThrowIfNull(rejected);
        var loaded = new List<WaveAnchor>();
        var claimed = new HashSet<(MapLevelReference Level, uint SettingsId)>();
        foreach (var document in ContentPackages.Read(bepInExRoot, ContentPackages.EngineReader, "forge", WaveAnchorManifest.FileNameOnly, ReadCodes, rejected))
        {
            if (!PublishedResources.TryRead(document, PublishedResources.MaximumFileBytes, ReadCodes, rejected, out var text))
                continue;
            if (!WaveAnchorManifest.TryParse(text!, out var parsed, out var code, out var reason))
            {
                rejected(PublishedResources.Diagnostic(document.Relative, code!, reason!));
                continue;
            }
            foreach (var refusal in parsed!.Refusals)
                rejected(PublishedResources.Diagnostic(document.Relative, refusal.Code, refusal.Reason));
            foreach (var anchor in parsed.Anchors)
            {
                if (!claimed.Add((anchor.Level, anchor.SettingsId)))
                {
                    rejected(PublishedResources.Diagnostic(document.Relative, "wave-anchor-duplicate-id",
                        "Wave settings id `" + anchor.SettingsId + "` is claimed for level `" + anchor.Level
                        + "` by more than one document; the later row is left out."));
                    continue;
                }
                loaded.Add(anchor);
            }
        }
        return Array.AsReadOnly(loaded.ToArray());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Detach(this);
    }
}
