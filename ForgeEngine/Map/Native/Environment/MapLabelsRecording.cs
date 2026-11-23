using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>The world signs' part in a recording, through ForgeCore's one interface for every mod: a sign a behaviour shows is told
/// as the state of <c>ForgeEngine/label/&lt;target&gt;</c> each time what it shows or where it stands changes (rendered text, icon,
/// colours, size, place, turn, facing), and null when it goes. A level that only shows a recording runs no behaviour, so the same
/// table draws a recorded sign itself (<see cref="BuildSign"/>), keeps it facing the camera when it did, and takes it away again.</summary>
internal static partial class MapLabels
{
    internal const string RecordingPrefix = "ForgeEngine/label/";
    private const char Separator = '\u001f';
    private static readonly Dictionary<string, Entry> Replayed = new(StringComparer.Ordinal);

    private static string RecordingKey(Entry entry) => RecordingPrefix + entry.Target.Id;

    private static void TellRecording(Entry entry)
    {
        if (entry.Hud) return;
        var p = entry.WrittenPosition; var q = entry.WrittenRotation;
        ForgeCore.Recording.State(RecordingKey(entry), string.Join(Separator,
            entry.RenderedText, entry.Style is { } style ? style.ToString() : "", PlacedModelSession.Hex(entry.TextColor), PlacedModelSession.Hex(entry.BackdropColor),
            F(entry.BackdropPadding), F(entry.WrittenScale), F(p.x) + "," + F(p.y) + "," + F(p.z),
            F(q.x) + "," + F(q.y) + "," + F(q.z) + "," + F(q.w), entry.CameraFacing ? "1" : "0"));
    }

    /// <summary>Answers for the signs in a level that only shows a recording; disposing it takes the answer back.</summary>
    internal static IDisposable ProvideRecording() => ForgeCore.Recording.Provide(RecordingPrefix, new RecordedSigns());

    private static void ClearReplayed()
    {
        foreach (var entry in Replayed.Values) Discard(entry);
        Replayed.Clear();
    }

    private sealed class RecordedSigns : ForgeCore.IRecordingProvider
    {
        public GameObject? Find(string key) => Replayed.TryGetValue(key, out var entry) && entry.Root is { WasCollected: false } root ? root : null;

        public bool Apply(string key, string? value)
        {
            if (Replayed.Remove(key, out var old)) Discard(old);
            if (value == null) return true;
            var parts = value.Split(Separator);
            if (parts.Length != 9 || !Vector(parts[6], out var at) || !Turn(parts[7], out var turn)) return false;
            eNavMarkerStyle? style = parts[1].Length == 0 ? null : Enum.TryParse<eNavMarkerStyle>(parts[1], out var parsed) ? parsed : null;
            var facing = parts[8] == "1";
            var entry = new Entry
            {
                CameraFacing = facing,
                Camera = facing ? MapCamera.Transform() : null,
                TextColor = PlacedModelSession.Colour(parts[2]) ?? Color.white,
                BackdropColor = PlacedModelSession.Colour(parts[3]) ?? default,
                BackdropPadding = Number(parts[4]),
                WrittenScale = Number(parts[5]),
                WrittenPosition = at,
                WrittenRotation = turn,
                RenderedText = parts[0]
            };
            try
            {
                entry.Root = new GameObject("ForgeWorldLabel");
                entry.RootTransform = entry.Root.transform;
                entry.RootTransform.SetPositionAndRotation(at, turn);
                entry.RootTransform.localScale = Vector3.one * entry.WrittenScale;
                BuildSign(entry, style, entry.RenderedText, entry.TextColor, entry.BackdropColor);
            }
            catch (Exception) { Discard(entry); return false; }
            Replayed[key] = entry;
            // A recorded sign that faced the camera faces the camera that showed the recording when the sign was drawn (each jump
            // draws it again); a camera gone since leaves it as it stands. The follow ends with the sign's root.
            if (entry.CameraFacing && !ForgeCore.NativeObjects.Gone(entry.Camera))
                ForgeVisual.CameraFacing.Follow(entry.RootTransform!, ForgeVisual.Facing.CameraTurn, entry.Camera);
            return true;
        }

        public void Placed(string key) { }
    }

    private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
    private static float Number(string s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;

    private static bool Vector(string s, out Vector3 v)
    {
        var p = s.Split(',');
        v = p.Length == 3 ? new Vector3(Number(p[0]), Number(p[1]), Number(p[2])) : default;
        return p.Length == 3;
    }

    private static bool Turn(string s, out Quaternion q)
    {
        var p = s.Split(',');
        q = p.Length == 4 ? new Quaternion(Number(p[0]), Number(p[1]), Number(p[2]), Number(p[3])) : Quaternion.identity;
        return p.Length == 4;
    }
}
