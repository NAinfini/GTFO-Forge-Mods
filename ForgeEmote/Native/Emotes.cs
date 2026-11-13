using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using ForgeAudio;
using ForgeCore;
using ForgeUI;
using ForgeVisual;
using Player;
using UnityEngine;

namespace ForgeEmote.Api;

/// <summary>
/// The actions the wheel offers. Depend on <c>NAinfini.ForgeEmote</c> and either put <c>*.emote.forge</c> files in a folder called <c>ForgeEmote</c>
/// beside your plugin, or call <see cref="Register"/> from your plugin's <c>Load</c>.
/// </summary>
public static class Emotes
{
    internal sealed class Entry
    {
        internal EmoteDefinition Definition = null!;
        internal JointClip Clip = null!;
        internal JointClip? FirstPerson;
        internal JointMask Mask = null!;
        /// <summary>What moves on the local player's body while the action is seen from the eye: the arms of <see cref="Mask"/>.</summary>
        internal JointMask FirstPersonMask = null!;
        internal uint NetId;
        internal string Hash = "";
        internal UITexts Texts = null!;
        /// <summary>The ForgeAudio library of the definition's folder, when the action has a <see cref="EmoteDefinition.Sound"/>.</summary>
        internal AudioLibrary? Audio;

        /// <summary>The action's sound from the body, at where the action is now: the same moment on every screen. A one-shot is heard only from its start (a late
        /// joiner sees the rest of the action without it); a loop joins where it is. Null without a sound or when ForgeAudio refused it (it logged why).</summary>
        internal AudioHandle? PlaySound(PlayerAgent agent, float start)
        {
            if (Audio == null) return null;
            var elapsed = Time.time - start - (Definition.Pair ? Definition.PairHold : 0f);
            if (!Definition.Loop && elapsed > SoundJoinSeconds) return null;
            var played = Audio.Play(Definition.Sound!, agent.transform, new PlayOptions { StartSeconds = Mathf.Max(0f, elapsed), IsSelf = agent.IsLocallyOwned });
            return played.Accepted ? played.Handle : null;
        }
    }

    /// <summary>How late a one-shot sound may still start: a network trip, not a late join.</summary>
    private const float SoundJoinSeconds = 0.5f;
    private static readonly List<Entry> All = new();
    private static readonly Dictionary<uint, Entry> ByNet = new();
    private static readonly Dictionary<string, AudioLibrary> Libraries = new();
    private static bool _closed;

    internal static IReadOnlyList<Entry> Entries => All;
    internal static Entry? Find(uint netId) => ByNet.TryGetValue(netId, out var entry) ? entry : null;
    internal static Entry? Find(string id) => All.FirstOrDefault(entry => entry.Definition.Id == id);

    internal static event Action? Changed;

    /// <summary>Adds an action; false with the reason when it is refused (a field that is not valid, named as a definition file would name it; a missing clip file; an ID taken or whose number clashes, or registration has closed: it closes on first entering a lobby).</summary>
    public static bool Register(EmoteDefinition definition, out string? error)
    {
        error = null;
        if (definition == null) { error = "definition is null"; return false; }
        if (_closed) { error = "registration has closed"; return false; }
        if (EmoteRules.Validate(definition) is { } bad) { error = definition.Id + ": " + bad.Message; return false; }
        try
        {
            var netId = Fnv.Hash32(definition.Id);
            if (ByNet.TryGetValue(netId, out var other)) { error = other.Definition.Id == definition.Id ? definition.Id + " is already registered" : definition.Id + " has the same number as " + other.Definition.Id; return false; }
            var bytes = File.ReadAllBytes(Path.Combine(definition.Folder, definition.Clip));
            var firstBytes = definition.FirstPersonClip == null ? null : File.ReadAllBytes(Path.Combine(definition.Folder, definition.FirstPersonClip));
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            sha.AppendData(bytes);
            if (firstBytes != null) sha.AppendData(firstBytes);
            if (definition.Sound != null) sha.AppendData(System.Text.Encoding.UTF8.GetBytes(definition.Sound));
            // What changes how the action plays on a teammate's screen is part of what both sides must share.
            sha.AppendData(System.Text.Encoding.UTF8.GetBytes(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{definition.Loop}|{definition.Body}|{definition.Pair}|{definition.PairHold}")));
            // Named, so a recording carries the action by name and a replay finds it again (ForgeCore Recording; the first-person clip is the local view only).
            var clip = JointClip.Read(bytes, Plugin.Id + "/" + definition.Id);
            if (definition.Pair && definition.PairHold >= clip.Duration) { error = definition.Id + ": PairHold must be less than the clip's length (" + clip.Duration.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " s)."; return false; }
            // Only a whole-body clip writes the hips, so only it can hover or sink (SeatOnFloor); the file's bytes (and so the content hash) stay as they are.
            if (definition.Body == "full") clip.SeatOnFloor();
            var entry = new Entry
            {
                Definition = definition,
                Clip = clip,
                FirstPerson = firstBytes == null ? null : JointClip.Read(firstBytes),
                Mask = MaskOf(definition.Body),
                FirstPersonMask = MaskOf(definition.Body is "left-arm" or "right-arm" ? definition.Body : "arms"),
                NetId = netId,
                Hash = Convert.ToHexString(sha.GetHashAndReset()),
                Texts = UIKit.Texts(Path.Combine(definition.Folder, "lang")),
                Audio = definition.Sound == null ? null : LibraryOf(definition),
            };
            All.Add(entry);
            ByNet.Add(netId, entry);
            Changed?.Invoke();
            return true;
        }
        catch (Exception problem) when (problem is IOException or FormatException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException or InvalidOperationException)
        {
            error = definition.Id + ": " + problem.Message;
            return false;
        }
    }

    internal static bool Closed => _closed;

    internal static void Close() => _closed = true;

    /// <summary>One ForgeAudio library per definition folder, named after the pack: its <c>audio</c> folder and <c>cues.forge</c>, loaded the first time an action of the folder has a sound.</summary>
    private static AudioLibrary LibraryOf(EmoteDefinition definition)
    {
        if (Libraries.TryGetValue(definition.Folder, out var library)) return library;
        var pack = System.Text.RegularExpressions.Regex.Replace(definition.Id[..definition.Id.IndexOf(':')], "[^A-Za-z0-9_.-]", "_");
        library = AudioKit.Load(Plugin.Id + "." + pack, Path.Combine(definition.Folder, "audio"));
        Libraries.Add(definition.Folder, library);
        return library;
    }

    private static JointMask MaskOf(string body) => body switch
    {
        "left-arm" => new JointMask().Add(BodyPart.LeftArm),
        "right-arm" => new JointMask().Add(BodyPart.RightArm),
        "arms" => new JointMask().Add(BodyPart.LeftArm).Add(BodyPart.RightArm),
        "upper" => JointMask.UpperBody,
        _ => JointMask.FullBody,
    };

    internal static void Load()
    {
        var own = Path.Combine(Path.GetDirectoryName(typeof(Emotes).Assembly.Location)!, "ForgeEmote", "Icons");
        var names = new List<string>();
        if (Directory.Exists(own)) foreach (var png in Directory.GetFiles(own, "*.png")) names.Add(Path.GetFileNameWithoutExtension(png));
        if (names.Count > 0) UIKit.Mod(Plugin.Id).Icons("ForgeEmote")?.RegisterFolder(own, names.ToArray());
        var errors = new List<string>();
        foreach (var folder in ContentFolders.Find("ForgeEmote", errors))
            foreach (var file in Directory.GetFiles(folder, "*.emote.forge"))
            {
                try
                {
                    foreach (var definition in EmoteFile.Parse(File.ReadAllText(file), folder))
                        if (!Register(definition, out var error)) Report(file, error!);
                }
                catch (Exception problem) when (problem is IOException or UnauthorizedAccessException or StrictJsonException or System.Text.Json.JsonException)
                {
                    Report(file, problem.Message);
                }
            }
        foreach (var error in errors) Plugin.Logger.Error("ForgeEmote: " + error);
    }

    private static void Report(string file, string reason)
    {
        Plugin.Logger.Error("ForgeEmote " + Path.GetFileName(file) + ": " + reason);
    }
}
