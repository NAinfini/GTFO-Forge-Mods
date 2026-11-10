using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeCore;
using ForgeEmote.Api;

namespace ForgeEmote;

/// <summary>Reads a definition file: an array, strictly (an unknown field or an out-of-range value names its path).</summary>
internal static class EmoteFile
{
    private static readonly string[] Fields = { "Id", "Name", "Icon", "Page", "Slot", "Clip", "FirstPersonClip", "Body", "Loop", "FadeIn", "FadeOut", "View", "Move", "HideHeld", "CancelOnHit", "Crude", "Comms", "Ping", "Sound", "Pair", "PairHold" };

    private static string Text(JsonElement value, string at, string field, string? fallback)
    {
        if (value.TryGetProperty(field, out var found))
        {
            if (found.ValueKind != JsonValueKind.String || found.GetString()!.Length == 0) throw new StrictJsonException(at + field, at + field + " must be text.");
            return found.GetString()!;
        }
        return fallback ?? throw new StrictJsonException(at + field, at + field + " is missing.");
    }

    /// <summary>The definitions in one file's text; <paramref name="folder"/> is where its files are.</summary>
    internal static List<EmoteDefinition> Parse(string json, string folder)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new StrictJsonException("", "A definition file is an array.");
        var all = new List<EmoteDefinition>();
        var index = 0;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var at = "[" + index++ + "].";
            StrictJson.OnlyKnown(item, at, Fields);
            var page = Text(item, at, "Page", null);
            var (tactical, gesture, dance) = (page == "tactical", page == "gesture", page == "dance");
            var body = Text(item, at, "Body", tactical ? "left-arm" : dance ? "full" : "upper");
            var id = Text(item, at, "Id", null);
            var view = Text(item, at, "View", dance ? "third-person" : "first-person");
            if (view is not ("first-person" or "third-person")) throw new StrictJsonException(at + "View", at + "View must be first-person or third-person.");
            var move = Text(item, at, "Move", tactical ? "free" : "locked");
            if (move is not ("free" or "locked")) throw new StrictJsonException(at + "Move", at + "Move must be free or locked.");
            var definition = new EmoteDefinition
            {
                Id = id,
                Name = Text(item, at, "Name", null),
                Icon = item.TryGetProperty("Icon", out _) ? Text(item, at, "Icon", null) : null,
                Page = page,
                Slot = (int)StrictJson.Integer(item, at, "Slot", 0, 0, EmoteRules.SlotsPerPage - 1),
                Clip = Text(item, at, "Clip", null),
                FirstPersonClip = item.TryGetProperty("FirstPersonClip", out _) ? Text(item, at, "FirstPersonClip", null) : null,
                Body = body,
                Loop = StrictJson.Bool(item, at, "Loop", dance),
                FadeIn = (float)StrictJson.Number(item, at, "FadeIn", 0.15, 0, 5),
                FadeOut = (float)StrictJson.Number(item, at, "FadeOut", 0.2, 0, 5),
                View = view == "first-person" ? EmoteView.FirstPerson : EmoteView.ThirdPerson,
                Move = move == "free" ? EmoteMove.Free : EmoteMove.Locked,
                HideHeld = StrictJson.Bool(item, at, "HideHeld", gesture || dance),
                CancelOnHit = StrictJson.Bool(item, at, "CancelOnHit", dance),
                Crude = StrictJson.Bool(item, at, "Crude", false),
                Comms = (uint)StrictJson.Integer(item, at, "Comms", 0, 0, uint.MaxValue),
                Ping = StrictJson.Bool(item, at, "Ping", false),
                Sound = item.TryGetProperty("Sound", out _) ? Text(item, at, "Sound", null) : null,
                Pair = StrictJson.Bool(item, at, "Pair", false),
                PairHold = (float)StrictJson.Number(item, at, "PairHold", 0, 0, 60),
                Folder = folder,
            };
            if (EmoteRules.Validate(definition) is { } bad) throw new StrictJsonException(at + bad.Field, at + bad.Message);
            all.Add(definition);
        }
        return all;
    }
}
