using System;
using System.Globalization;
using Localization;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>The placed models' part in a recording, through ForgeCore's one interface for every mod: each model is declared under
/// <c>ForgeEngine/model/&lt;id&gt;</c> when it is placed (the recorder follows its place, size and shown flag whoever changes them), the text this client
/// shows on it is told as the model's own state whenever it is shown (rendered, with the colour and backdrop a behaviour put over it), and in a level
/// that only shows a recording the same session finds its models by those keys and draws a recorded text through its own <see cref="Show"/>.</summary>
internal sealed partial class PlacedModelSession
{
    internal const string RecordingPrefix = "ForgeEngine/model/";
    private const char Separator = '\u001f';
    private readonly IDisposable _recordingProvider;

    internal static string RecordingKey(string id) => RecordingPrefix + id;

    // The text shown, then the colour and the backdrop colour put over it (RRGGBBAA, empty: the model's own).
    private static void TellRecording(Entry entry, string text)
        => ForgeCore.Recording.State(RecordingKey(entry.Instance.Model.Id), text + Separator + Hex(entry.ColorOverride) + Separator + Hex(entry.BackdropOverride));

    /// <summary>A colour as RRGGBBAA; empty for none. Shared with the world signs' part in a recording.</summary>
    internal static string Hex(Color? colour)
    {
        if (colour is not { } value) return "";
        Color32 c = value;
        return c.r.ToString("X2", CultureInfo.InvariantCulture) + c.g.ToString("X2", CultureInfo.InvariantCulture) + c.b.ToString("X2", CultureInfo.InvariantCulture) + c.a.ToString("X2", CultureInfo.InvariantCulture);
    }

    internal static Color? Colour(string hex)
    {
        if (hex.Length != 8 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return null;
        return new Color32((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    private Entry? RecordedEntry(string key)
        => !_disposed && key.StartsWith(RecordingPrefix, StringComparison.Ordinal) && _entries.TryGetValue(key.Substring(RecordingPrefix.Length), out var entry)
            && entry.Root != null && !entry.Root.WasCollected ? entry : null;

    private sealed class RecordedModels : ForgeCore.IRecordingProvider
    {
        private readonly PlacedModelSession _session;

        internal RecordedModels(PlacedModelSession session) => _session = session;

        public GameObject? Find(string key) => _session.RecordedEntry(key)?.Root;

        public bool Apply(string key, string? value)
        {
            if (_session.RecordedEntry(key) is not { } entry) return false;
            entry.EffectiveAtDisplayKnown = false;
            if (value == null)
            {
                // Back to the model's own text and look, shown the way placing it showed it.
                entry.TextOverride = null; entry.ColorOverride = null; entry.BackdropOverride = null;
                DestroyLabel(entry);
                var own = entry.Instance.Model.Text;
                if (own == null) return true;
                var language = default(Language);
                if (StationTextValue.RequiresLanguage(own) && !_session.TryEffectiveLanguage(out language)) return true;
                Show(entry, language, true, out _);
                _session.TrackBillboard(entry);
                return true;
            }
            var parts = value.Split(Separator);
            entry.TextOverride = parts[0];
            entry.ColorOverride = parts.Length > 1 ? Colour(parts[1]) : null;
            entry.BackdropOverride = parts.Length > 2 ? Colour(parts[2]) : null;
            if (!Show(entry, default, false, out _)) return false;
            _session.TrackBillboard(entry);
            return true;
        }

        public void Placed(string key)
        {
            if (_session.RecordedEntry(key) is { } entry) FollowDetachedLabel(entry);
        }
    }
}
