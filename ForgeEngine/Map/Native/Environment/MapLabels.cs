using System;
using System.Collections.Generic;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using ForgeEngine.Presentation.Native;
using ForgeVisual;
using LevelGeneration;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeMap.Native;

/// <summary>Local displays delivered by the existing Forge presentation channel. Exact objects are retained;
/// no address is resolved again during rendering. The session's one MapClock owns updates and world cleanup.</summary>
internal static partial class MapLabels
{
    internal sealed class Entry
    {
        /// <summary>The navigation icon the sign shows beside its text, if any.</summary>
        internal eNavMarkerStyle? Style;
        /// <summary>`Time.time` after which a HUD marker removes itself; 0 keeps it until it is removed.</summary>
        internal float ExpiresAt;
        internal EntityReference Target = null!;
        internal bool Hud;
        internal GameObject Root = null!;
        internal Transform RootTransform = null!;
        internal Transform? ReceiverTransform;
        internal Transform? Camera;
        internal GameObject? Receiver;
        internal Component? Owner;
        internal TriggerZone? Zone;
        internal LG_Zone? ScopeZone;
        internal ItemReplicator? Item;
        internal InteractionPoint? Point;
        internal Vector3 Offset;
        internal bool CameraFacing;
        internal bool UsesPlayerLanguage;
        internal Vector3 WrittenPosition;
        internal Quaternion WrittenRotation;
        internal float WrittenScale;
        /// <summary>The text colour the sign was shown or last restyled with.</summary>
        internal Color TextColor;
        internal NavMarker? Marker;
        internal Material? IconMaterial;
        internal WorldBackdrop? Backdrop;
        internal float BackdropPadding = (float)PlacedModelTextStyle.DefaultBackgroundPadding;
        /// <summary>The backdrop colour the sign was shown or last restyled with; opacity 0 is no backdrop.</summary>
        internal Color BackdropColor;
        internal string TextReference = "";
        internal WorldText? Body;
        internal TextMeshPro? Text => Body?.Text;
        internal Vector2 TextSize;
        internal string RenderedText = "";
        internal Localization.Language TextLanguage;
        internal bool TextLanguageKnown;
        internal string? LanguageFailure;
    }
    private static readonly HandlerSlot W_target = WorldLabelContract.Shape.InputSlot("target");
    private static readonly HandlerSlot W_enabled = WorldLabelContract.Shape.InputSlot("enabled");
    private static readonly HandlerSlot W_style = WorldLabelContract.Shape.InputSlot("style");
    private static readonly HandlerSlot W_text = WorldLabelContract.Shape.InputSlot("text");
    private static readonly HandlerSlot W_position = WorldLabelContract.Shape.InputSlot("position");
    private static readonly HandlerSlot W_offset = WorldLabelContract.Shape.InputSlot("offset");
    private static readonly HandlerSlot W_text_resource = WorldLabelContract.Shape.InputSlot("text_resource");
    private static readonly HandlerSlot W_size = WorldLabelContract.Shape.InputSlot("size");
    private static readonly HandlerSlot W_color = WorldLabelContract.Shape.InputSlot("color");
    private static readonly HandlerSlot W_rotation = WorldLabelContract.Shape.InputSlot("rotation");
    private static readonly HandlerSlot W_background_padding = WorldLabelContract.Shape.InputSlot("background_padding");
    private static readonly HandlerSlot W_background_color = WorldLabelContract.Shape.InputSlot("background_color");
    private static readonly HandlerSlot W_background_opacity = WorldLabelContract.Shape.InputSlot("background_opacity");
    private static readonly HandlerSlot H_style = EnvironmentContract.NavMarkerShape.InputSlot("style");
    private static readonly HandlerSlot H_text = EnvironmentContract.NavMarkerShape.InputSlot("text");
    private static readonly HandlerSlot H_position = EnvironmentContract.NavMarkerShape.InputSlot("position");
    private static readonly HandlerSlot H_offset = EnvironmentContract.NavMarkerShape.InputSlot("offset");
    private static readonly HandlerSlot Facing = WorldLabelContract.Shape.ParameterSlot("facing");
    private static readonly List<Entry> Entries = new();
    // A display that is shown and lost on every refresh would write the same two rows each time. Each distinct row
    // (display, phase, reason) is written in full this many times; the rest are counted and reported once by Clear.
    private const int RepeatedRecordLimit = 4;
    private readonly record struct RecordKey(bool Hud, string Entity, string Phase, string Reason);
    private static readonly Dictionary<RecordKey, int> RecordCounts = new();
    internal static bool Active => Entries.Count != 0;

    private static readonly HandlerSlot W_viewers = WorldLabelContract.Shape.InputSlot("viewers");

    /// <summary>The `world_label` command: the label is shown on this client when one of the viewers is local, and
    /// every viewer's row carries the same answer.</summary>
    internal static Outcome WorldLabel(CommandContext context, IPlayerDirectory players)
    {
        var answer = WorldLabelAnswer(context, players, out bool shown);
        if (!shown) return answer;
        return PresentationRows.Answer(context, answer.Status, answer.Code);
    }

    /// <summary>What the label command answers, without any row: <paramref name="shown"/> says the request reached
    /// this client's display (otherwise the answer is a refusal that owes no row). It is also what a recipient's
    /// retained presentation replays outside a command invocation (<see cref="MapRecipientPresentationState"/>),
    /// where there is no row writer to write on.</summary>
    internal static Outcome WorldLabelAnswer(CommandContext context, IPlayerDirectory players, out bool shown)
    {
        shown = false;
        int count = context.Count(W_viewers);
        if (count < 0) return Outcome.Rejected(ResultCode.MissingField);
        if (count > CommandResult.MaximumFacts) return Outcome.Rejected(ResultCode.TooManyTargets);
        var viewers = new EntityReference[count];
        for (int i = 0; i < count; i++)
        {
            if (!context.TryEntity(W_viewers, i, out viewers[i])) return Outcome.Rejected(ResultCode.UnsupportedRecipient);
            if (viewers[i].WorldEpoch != context.WorldEpoch) return Outcome.Rejected(ResultCode.StaleEntity);
        }
        var sessions = players.SessionsOf(viewers);
        bool local = false;
        if (sessions != null) foreach (var session in sessions) local |= players.IsLocal(session);
        if (!local) return Outcome.Rejected(ResultCode.WorldLabelNotAddressed);
        shown = true;
        return Handle(context, false, viewers);
    }

    internal static Outcome HudMarker(CommandContext context)
    {
        var answer = Handle(context, true);
        return PresentationRows.Answer(context, answer.Status, answer.Code);
    }

    /// <summary>The HUD marker's audience: null when the step has no `viewers` (every client shows it) or this client is
    /// one of them, otherwise the refusal that skips this client. A stale viewer refuses the whole step by name. The port
    /// is optional, so an unwired one (count -1) and an empty one both mean every client.</summary>
    internal static CommandResult? HudAudience(CommandContext context, IPlayerDirectory players)
    {
        int count = context.Count(EnvironmentContract.NavMarkerViewers);
        if (count <= 0) return null;
        if (count > CommandResult.MaximumFacts) return CommandResult.Rejected(ResultCode.TooManyTargets);
        var viewers = new EntityReference[count];
        for (int i = 0; i < count; i++)
        {
            if (!context.TryEntity(EnvironmentContract.NavMarkerViewers, i, out viewers[i])) return CommandResult.Rejected(ResultCode.UnsupportedRecipient);
            if (viewers[i].WorldEpoch != context.WorldEpoch) return CommandResult.Rejected(ResultCode.StaleEntity);
        }
        var sessions = players.SessionsOf(viewers);
        if (sessions != null) foreach (var session in sessions) if (players.IsLocal(session)) return null;
        return CommandResult.Rejected(ResultCode.HudMarkerNotAddressed);
    }

    private static Outcome Handle(CommandContext context, bool hud, IReadOnlyList<EntityReference>? viewers = null)
    {
        if (!context.TryEntity(hud ? EnvironmentContract.NavMarkerZone : W_target, out var target))
            return Outcome.Rejected(ResultCode.MarkerTargetRequired);
        if (target.WorldEpoch != context.WorldEpoch)
        { Remove(target, hud, "stale-world"); return Result(target, hud, false, ResultCode.StaleEntity); }
        if (context.Boolean(hud ? EnvironmentContract.NavMarkerEnabled : W_enabled) is not { } enabled)
            return Result(target, hud, false, ResultCode.MarkerEnabledRequired);
        if (!enabled)
        {
            if (!hud && (context.EntityInstance(target) as TriggerZone ?? MapHost.Session?.TriggerZones?.LabelReceiver(target)) is { } hiddenZone)
                hiddenZone.LabelOverridden = true;
            Remove(target, hud, "requested"); return Result(target, hud, true, ResultCode.MarkerHidden);
        }

        string text = context.Text(hud ? H_text : W_text) ?? "";
        if (!hud && context.TryResource(W_text_resource, out var resource))
        {
            if (text.Length != 0 || resource.ResourceKind != TextResourceContract.ResourceKind
                || !uint.TryParse(resource.ResourceId, out var id)) return Result(target, hud, false, ResultCode.MarkerTextInvalid);
            // Original Text blocks use English as their primary column. Keep the id, so the same local
            // language reader also handles player language overrides and subsequent language changes.
            text = TextResourceContract.ReferencePrefix + id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "@English";
        }
        string textReference = text;
        bool usesPlayerLanguage = StationTextValue.RequiresLanguage(textReference);
        Localization.Language textLanguage = default;
        if (usesPlayerLanguage)
        {
            try { textLanguage = StationTextValue.CurrentLanguage; }
            catch (RuntimeContractException error) { return Result(target, hud, false, error.Code); }
        }
        if (!(usesPlayerLanguage
                ? StationTextValue.TryRender(text, textLanguage, out text)
                : StationTextValue.TryRender(text, out text)))
            return Result(target, hud, false, ResultCode.MarkerTextUnavailable);
        if (hud && !SingleLine(text)) return Result(target, hud, false, ResultCode.MarkerTextMustBeSingleLine);
        var styleText = context.Text(hud ? H_style : W_style);
        eNavMarkerStyle? style = null;
        if (styleText != null)
        {
            if (!Enum.TryParse<eNavMarkerStyle>(styleText, out var parsed) || !Enum.IsDefined(typeof(eNavMarkerStyle), parsed))
                return Result(target, hud, false, ResultCode.MarkerStyleInvalid);
            style = parsed;
        }
        else if (hud) style = eNavMarkerStyle.PlayerPingPickupObjectiveItem;
        if (!hud && style == null && text.Length == 0) return Result(target, hud, false, ResultCode.MarkerContentRequired);
        var positionValue = context.Vector(hud ? H_position : W_position);
        var offsetValue = context.Vector(hud ? H_offset : W_offset);
        if (positionValue != null && !GameVectors.TryUnity(positionValue, out _)
            || offsetValue != null && !GameVectors.TryUnity(offsetValue, out _))
            return Result(target, hud, false, ResultCode.MarkerPositionInvalid);
        Vector3 offset = offsetValue is { } authored ? new((float)authored.X, (float)authored.Y, (float)authored.Z)
            : new(0, hud ? 0 : 1.5f, 0);
        GameObject? receiver = null; Component? owner = null; TriggerZone? zone = null; LG_Zone? scopeZone = null; ItemReplicator? item = null;
        InteractionPoint? authoredPoint = null;
        Vector3 position;
        if (positionValue is { } point)
        {
            // A point is explicitly scoped by its addressed zone. An object + absolute point is ambiguous.
            if (EnvironmentZone.Parse(target) is not { } address) return Result(target, hud, false, ResultCode.MarkerPointScopeRequired);
            scopeZone = ZoneIndex.Current(context.WorldEpoch).ZoneAt(new ZoneCoordinates(address.Dimension, address.Layer, address.Zone));
            if (scopeZone == null || scopeZone.WasCollected) return Result(target, hud, false, ResultCode.MarkerTargetUnavailable);
            position = new((float)point.X, (float)point.Y, (float)point.Z);
        }
        else
        {
            object? native = LevelObjectModule.IsCapturedInstance(target)
                ? MapHost.Session?.Levels?.NativeInstance(target) : context.EntityInstance(target);
            if (native == null || native is PlacedModelInstance)
                native = PlacedModelSession.LabelReceiver(target);
            native ??= TerminalObjectActions.ResolveByAddress(target);
            native ??= DoorActionCommands.ResolveByAddress(target);
            native ??= GeneratorObservation.LabelReceiver(target);
            native ??= MapHost.Session?.TriggerZones?.LabelReceiver(target);
            native ??= MapHost.Session?.InteractionPoints?.LabelReceiver(target);
            if (native is TriggerZone trigger) zone = trigger;
            // An interaction point is data with a world pose, not a native component: it is the position itself.
            else if (native is InteractionPoint placedPoint && placedPoint.Placed) authoredPoint = placedPoint;
            // A wrapper the game has collected throws on first member access; refuse it by name instead.
            else if (native is ItemReplicator replicator)
            {
                if (replicator.WasCollected) return Stale(target, hud);
                item = replicator; receiver = PlacedItem(replicator);
            }
            else if (native is LG_PickupItem_Sync pickup)
            {
                if (pickup.WasCollected) return Stale(target, hud);
                owner = pickup; receiver = PlacedItem(pickup);
            }
            else if (native is Component component)
            {
                if (component.WasCollected) return Stale(target, hud);
                owner = component; receiver = component.gameObject;
            }
            if (zone == null && authoredPoint == null && (receiver == null || receiver.WasCollected))
            { Remove(target, hud, "receiver-lost"); return Result(target, hud, false, ResultCode.MarkerTargetUnavailable); }
            position = zone != null ? new((float)zone.Position[0], (float)(zone.Position[1] + zone.TopHeight()), (float)zone.Position[2])
                : authoredPoint != null ? new((float)authoredPoint.Position[0], (float)authoredPoint.Position[1], (float)authoredPoint.Position[2])
                : receiver!.transform.position;
        }
        if (hud && !ForgeCore.NavMarkers.Available) return Result(target, hud, false, ResultCode.MarkerLayerUnavailable);
        float expiresAt = 0;
        if (hud && context.Number(EnvironmentContract.NavMarkerDuration) is { } ticks)
        {
            double seconds = ticks / context.TicksPerSecond;
            if (!(seconds > 0) || seconds > 86400) return Result(target, hud, false, ResultCode.MarkerDurationInvalid);
            expiresAt = Time.time + (float)seconds;
        }
        float size = 1; Color color = Color.white; bool facing = false; Quaternion rotation = Quaternion.identity;
        Color backdrop = new(0, 0, 0, 0); double backdropPadding = PlacedModelTextStyle.DefaultBackgroundPadding;
        if (!hud)
        {
            var value = context.Number(W_size) ?? 1;
            // The card's own bound: a sign smaller than 0.01 (or mirrored by a negative size) is not one a player sees.
            if (!double.IsFinite(value) || value < 0.01 || value > float.MaxValue) return Result(target, hud, false, ResultCode.MarkerSizeInvalid);
            size = (float)value;
            var rgb = context.Vector(W_color);
            if (rgb is { } c)
            {
                if (!GameVectors.TryUnity(rgb, out _))
                    return Result(target, hud, false, ResultCode.MarkerColorInvalid);
                color = new((float)c.X, (float)c.Y, (float)c.Z, 1);
            }
            string? direction = context.Text(Facing);
            if (direction != "camera" && direction != "fixed") return Result(target, hud, false, ResultCode.MarkerFacingInvalid);
            facing = direction == "camera";
            // A fixed label turns with what it hangs on, the way a placed model turns with its room: the authored
            // angles are in the target's own frame, so the room's turn in the generated level never reverses the words.
            rotation = zone != null ? new Quaternion((float)zone.Rotation[0], (float)zone.Rotation[1], (float)zone.Rotation[2], (float)zone.Rotation[3])
                : authoredPoint != null ? new Quaternion((float)authoredPoint.Rotation[0], (float)authoredPoint.Rotation[1], (float)authoredPoint.Rotation[2], (float)authoredPoint.Rotation[3])
                : receiver != null ? receiver.transform.rotation : Quaternion.identity;
            if (context.Vector(W_rotation) is { } angles)
            {
                if (!GameVectors.TryUnity(angles, out _)) return Result(target, hud, false, ResultCode.MarkerRotationInvalid);
                rotation *= Quaternion.Euler((float)angles.X, (float)angles.Y, (float)angles.Z);
            }
            // Opacity 0 is no backdrop at all; the colour alone never draws anything.
            var opacity = context.Number(W_background_opacity) ?? 0;
            if (!double.IsFinite(opacity) || opacity < 0 || opacity > 1) return Result(target, hud, false, ResultCode.MarkerBackgroundInvalid);
            var backdropRgb = context.Vector(W_background_color);
            if (backdropRgb is { } b && !GameVectors.TryUnity(backdropRgb, out _))
                return Result(target, hud, false, ResultCode.MarkerBackgroundInvalid);
            backdrop = backdropRgb is { } rgbValue ? new((float)rgbValue.X, (float)rgbValue.Y, (float)rgbValue.Z, (float)opacity) : new(0, 0, 0, (float)opacity);
            backdropPadding = context.Number(W_background_padding) ?? PlacedModelTextStyle.DefaultBackgroundPadding;
            if (!double.IsFinite(backdropPadding) || backdropPadding < 0 || backdropPadding > float.MaxValue) return Result(target, hud, false, ResultCode.MarkerBackgroundInvalid);
        }
        // The same sign refreshed for the same receiver is edited where it stands; a new sign (or any change of kind,
        // receiver, icon or facing target) is built below.
        if (!hud && style == null && text.Length != 0 && !LevelObjectModule.IsCapturedInstance(target))
        {
            var wantedCamera = facing ? MapCamera.Transform() : null;
            if (facing && wantedCamera == null) return Result(target, hud, false, ResultCode.MarkerCameraUnavailable);
            Vector3 wanted = position + offset;
            if (!ForgeCore.Vec.Finite(wanted)) return Result(target, hud, false, ResultCode.MarkerPositionInvalid);
            if (TryReuse(context.WorldEpoch, target, receiver, owner, zone, scopeZone, item, authoredPoint, wantedCamera, wanted, offset, size, color,
                    facing, rotation, backdrop, (float)backdropPadding, textReference, text, usesPlayerLanguage, textLanguage))
                return Result(target, hud, true, ResultCode.MarkerShown);
        }
        var entry = new Entry
        {
            ExpiresAt = expiresAt,
            Target = target,
            Hud = hud,
            Receiver = receiver,
            Owner = owner,
            Zone = zone,
            ScopeZone = scopeZone,
            Item = item,
            Point = authoredPoint,
            Offset = offset,
            CameraFacing = facing,
            ReceiverTransform = receiver?.transform,
            Camera = facing ? MapCamera.Transform() : null,
            TextReference = textReference,
            RenderedText = text,
            TextLanguage = textLanguage,
            TextLanguageKnown = usesPlayerLanguage,
            UsesPlayerLanguage = usesPlayerLanguage,
            BackdropPadding = (float)backdropPadding,
            BackdropColor = backdrop,
            WrittenScale = size,
            TextColor = color
        };
        if (facing && entry.Camera == null) return Result(target, hud, false, ResultCode.MarkerCameraUnavailable);
        Vector3 placement = position + offset;
        if (!ForgeCore.Vec.Finite(placement)) return Result(target, hud, false, ResultCode.MarkerPositionInvalid);
        try
        {
            entry.Root = new GameObject(hud ? "ForgeHudMarker" : "ForgeWorldLabel");
            entry.RootTransform = entry.Root.transform;
            entry.RootTransform.position = placement;
            entry.WrittenPosition = placement;
            entry.WrittenRotation = facing ? entry.Camera!.rotation : rotation;
            entry.RootTransform.rotation = entry.WrittenRotation;
            entry.RootTransform.localScale = Vector3.one * size;
            if (hud)
            {
                var tracking = receiver != null && offset.x == 0 && offset.y == 0 && offset.z == 0
                    ? receiver : entry.Root;
                entry.Marker = ForgeCore.NavMarkers.Place(NavMarkerOption.Title | NavMarkerOption.Distance, tracking, text);
                if (entry.Marker == null || entry.Marker.WasCollected) throw new InvalidOperationException(ResultCode.MarkerPlacementRefused);
                if (style is { } icon) entry.Marker.SetStyle(icon);
                // SetStyle also changes the stock title options, so explicitly keep the author's title enabled.
                var options = entry.Marker.m_stateOptions;
                if (options == null || options.Length < 4) throw new InvalidOperationException(ResultCode.MarkerStyleStateUnavailable);
                entry.Marker.SetVisualStates(options[(int)NavMarkerState.Visible] | NavMarkerOption.Title | NavMarkerOption.Distance,
                    options[(int)NavMarkerState.InFocus] | NavMarkerOption.Title | NavMarkerOption.Distance,
                    options[(int)NavMarkerState.OnEdge], options[(int)NavMarkerState.Inactive]);
                entry.Marker.m_title.SetText(text);
                entry.Marker.m_title.richText = false;
                entry.Marker.m_title.enableWordWrapping = false;
                entry.Marker.m_title.lineSpacing = PlacedModelTextStyle.LineSpacing;
                entry.Marker.m_title.rectTransform.sizeDelta = entry.Marker.m_title.GetPreferredValues(text);
            }
            else BuildSign(entry, style, text, color, backdrop);
            // A display that replaces a live one of the same kind stays in the `shown` phase: only a change of phase
            // (first shown, removed) is a record. A counter that rewrites its text each refresh must not log each time.
            var replacing = false;
            for (int i = 0; i < Entries.Count; i++)
                if (Entries[i].Target == target && Entries[i].Hud == hud) { replacing = true; break; }
            Remove(target, hud, "replaced");
            if (LevelObjectModule.IsCapturedInstance(target))
            {
                bool owned = false;
                for (int i = 0; i < Entries.Count; i++) if (Entries[i].Target == target) { owned = true; break; }
                // The captured instance owns one combined release callback. A second visual joins that callback
                // without replacing it, so displaying a HUD marker never destroys the sign on the same item.
                if (!owned && MapHost.Session?.Levels?.OwnInstancePresentation(target, () => RemoveAll(target),
                    value => entry.Receiver != null && !entry.Receiver.WasCollected
                        // Pickup state changes already invalidate this presentation through ItemStateChanged.
                        // The camera sweep only checks native lifetime/view replacement; GetCurrentState creates
                        // a state wrapper, so reading it again every camera update defeats that event path.
                        && (value is not ItemReplicator pickup || pickup.Item?.TryCast<ItemInLevel>() is { WasCollected: false } view
                            && view.gameObject?.Pointer == entry.Receiver.Pointer)
                        && (value is not LG_PickupItem_Sync fixedPickup || fixedPickup.item is { WasCollected: false } fixedView
                            && fixedView.gameObject?.Pointer == entry.Receiver.Pointer)) != true)
                    throw new InvalidOperationException(ResultCode.StaleEntity);
            }
            Entries.Add(entry);
            // ForgeVisual turns a camera-facing sign just before the cameras render, until its root is destroyed with the entry.
            if (facing) ForgeVisual.CameraFacing.Follow(entry.RootTransform, ForgeVisual.Facing.CameraTurn, entry.Camera);
            if (!hud && entry.Text != null) StationSignPlacementReadback.Record(context.WorldEpoch, target, authoredPoint, owner, entry.RootTransform, entry.Text);
            if (!hud && zone != null) zone.LabelOverridden = true;
            if (!replacing) Record(entry, "shown", "marker-shown");
            TellRecording(entry);
            return Result(target, hud, true, ResultCode.MarkerShown);
        }
        catch (Exception error)
        {
            Entries.Remove(entry); Release(entry, "placement-failed");
            string code = error.Message is ResultCode.MarkerPlacementRefused or ResultCode.MarkerIconUnavailable
                or ResultCode.MarkerIconShaderUnavailable or ResultCode.MarkerBackgroundShaderUnavailable or ResultCode.MarkerStyleStateUnavailable or ResultCode.StaleEntity
                ? error.Message : ResultCode.MarkerPlacementRefused;
            return Result(target, hud, false, code);
        }
    }

    private static bool SameColor(Color a, Color b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;

    /// <summary>Edits the one live world sign of <paramref name="target"/> to what a fresh sign would show, when it has the
    /// same receiver, facing and no icon. False (nothing changed) when there is no such sign or it is not exactly that
    /// kind, so the caller builds a new one. Writes only what differs, so a refresh with unchanged content allocates nothing native.</summary>
    private static bool TryReuse(long epoch, EntityReference target, GameObject? receiver, Component? owner, TriggerZone? zone, LG_Zone? scopeZone,
        ItemReplicator? item, InteractionPoint? point, Transform? camera, Vector3 placement, Vector3 offset, float size, Color color, bool facing,
        Quaternion rotation, Color backdrop, float backdropPadding, string textReference, string text, bool usesPlayerLanguage, Localization.Language language)
    {
        Entry? found = null;
        for (int i = 0; i < Entries.Count; i++)
        {
            var each = Entries[i];
            if (each.Hud || each.Target != target) continue;
            if (found != null) return false;
            found = each;
        }
        if (found is not { Body: { Alive: true }, Marker: null, IconMaterial: null } e || e.Text == null || e.Text.WasCollected) return false;
        if (!ReferenceEquals(e.Zone, zone) || !ReferenceEquals(e.Point, point) || e.CameraFacing != facing
            || !ForgeCore.NativeObjects.Same(e.Receiver, receiver) || !ForgeCore.NativeObjects.Same(e.Owner, owner) || !ForgeCore.NativeObjects.Same(e.Item, item)
            || !ForgeCore.NativeObjects.Same(e.ScopeZone, scopeZone) || facing && !ForgeCore.NativeObjects.Same(e.Camera, camera)) return false;
        if (LostReason(e, epoch) != null) return false;
        var root = e.RootTransform;
        e.Offset = offset; e.ExpiresAt = 0; e.LanguageFailure = null;
        if (e.WrittenScale != size) { root.localScale = Vector3.one * size; e.WrittenScale = size; }
        if (!Same(e.WrittenPosition, placement)) { root.position = placement; e.WrittenPosition = placement; }
        var wantedRotation = facing ? e.Camera!.rotation : rotation;
        if (!Same(e.WrittenRotation, wantedRotation)) { root.rotation = wantedRotation; e.WrittenRotation = wantedRotation; }
        if (!SameColor(e.TextColor, color)) { e.Text.color = color; e.TextColor = color; }
        var textChanged = text != e.RenderedText;
        e.TextReference = textReference; e.UsesPlayerLanguage = usesPlayerLanguage;
        e.TextLanguage = language; e.TextLanguageKnown = usesPlayerLanguage; e.RenderedText = text;
        var paddingChanged = e.BackdropPadding != backdropPadding;
        e.BackdropPadding = backdropPadding;
        if (textChanged) LayoutText(e, text);
        if (backdrop.a > 0)
        {
            if (e.Backdrop == null) CreateBackground(e, backdrop);
            else
            {
                if (!SameColor(e.BackdropColor, backdrop)) e.Backdrop.SetColor(backdrop);
                if (paddingChanged && !textChanged) LayoutBackground(e);
            }
        }
        else if (e.Backdrop != null) { e.Backdrop.Destroy(); e.Backdrop = null; }
        e.BackdropColor = backdrop;
        StationSignPlacementReadback.Record(epoch, target, point, owner, root, e.Text);
        if (zone != null) zone.LabelOverridden = true;
        TellRecording(e);
        return true;
    }

    /// <summary>A world sign's body under <c>entry.Root</c>: the icon, the text and the backdrop, as a behaviour shows it and as a
    /// recording draws it again.</summary>
    private static void BuildSign(Entry entry, eNavMarkerStyle? style, string text, Color color, Color backdrop)
    {
        entry.Style = style;
        if (style is { } icon) CreateIcon(entry, icon, color);
        if (text.Length != 0)
        {
            // Match placed-model labels' finite default width. The measured height grows for wrapping
            // and explicit line breaks, while the outer transform keeps the author's size multiplier.
            // The authored offset is the board's lower edge. A board centred on it would sink half of
            // a tall list of steps into the floor, so the board grows upward from its pivot (the text default).
            entry.Body = WorldText.Create(entry.RootTransform, "text",
                new WorldTextStyle { FontSize = 3, Color = color, LineSpacing = PlacedModelTextStyle.LineSpacing, SceneDepth = true });
            entry.Body.SetShown(true);
            LayoutText(entry, text);
        }
        if (backdrop.a > 0 && (entry.Text != null || entry.IconMaterial != null)) CreateBackground(entry, backdrop);
    }

    private static Outcome Stale(EntityReference target, bool hud)
    { Remove(target, hud, "receiver-lost"); return Result(target, hud, false, ResultCode.StaleEntity); }

    // Native SetStyle / SetState select the actual visible sprite composition. No second style-to-icon table.
    private static void CreateIcon(Entry entry, eNavMarkerStyle style, Color color)
    {
        var sample = ForgeCore.NavMarkers.Place(NavMarkerOption.Empty, entry.Root, "");
        if (sample == null) throw new InvalidOperationException(ResultCode.MarkerIconUnavailable);
        try
        {
            sample.SetStyle(style); sample.SetVisible(true); sample.SetState(NavMarkerState.Visible);
            var holder = sample.m_iconHolder;
            if (holder == null) throw new InvalidOperationException(ResultCode.MarkerIconUnavailable);
            var parts = new NavMarkerComponent?[] {
                sample.m_playerSubObj, sample.m_waypointSubObj, sample.m_selectionSubObj, sample.m_edgeSubObj, sample.m_distanceSubObj, sample.m_titleSubObj, sample.m_playerNameSubObj, sample.m_pingSubObj, sample.m_enemySubObj, sample.m_droneDiscoverySubObj, sample.m_lookAt, sample.m_ammo, sample.m_health, sample.m_loot, sample.m_terminal, sample.m_caution, sample.m_hsu, sample.m_door, sample.m_playerInCompass, sample.m_sign, sample.m_resourceLocker, sample.m_resourceBox, sample.m_generator, sample.m_disinfection, sample.m_carryitem, sample.m_bulkheadMain, sample.m_bulkheadSecondary, sample.m_bulkheadThird, sample.m_consumables, sample.m_objectivePickup, sample.m_toolRefill, sample.m_securityDoor, sample.m_bulkheadDoor, sample.m_apexDoor, sample.m_bloodDoor, sample.m_securityCheckpointDoor, sample.m_bulkheadCheckpointDoor, sample.m_apexCheckpointDoor, sample.m_bloodCheckpointDoor, sample.m_bulkheadDC, sample.m_pin, sample.m_pinSelected, sample.m_pingObj
            };
            var sources = new List<SpriteRenderer>();
            for (int i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (part == null || !part.m_enabled) continue;
                part.CheckSprites();
                var sprites = part.m_sprites;
                if (sprites == null) continue;
                for (int j = 0; j < sprites.Length; j++) if (sprites[j]?.sprite != null) sources.Add(sprites[j]);
            }
            var basis = holder.lossyScale;
            if (basis.x == 0 || basis.y == 0 || basis.z == 0) throw new InvalidOperationException(ResultCode.MarkerIconUnavailable);
            float extent = 0;
            for (int i = 0; i < sources.Count; i++)
            {
                var source = sources[i];
                if (source == null || source.sprite == null) continue;
                var p = holder.InverseTransformPoint(source.transform.position);
                var s = source.transform.lossyScale; var bounds = source.sprite.bounds.size;
                extent = Math.Max(extent, Math.Max(Math.Abs(p.x) * 2 + bounds.x * Math.Abs(s.x / basis.x),
                    Math.Abs(p.y) * 2 + bounds.y * Math.Abs(s.y / basis.y)));
            }
            if (extent <= 0 || !float.IsFinite(extent)) throw new InvalidOperationException(ResultCode.MarkerIconUnavailable);
            var shader = ForgeVisual.Shaders.Find("Sprites/Default") ?? throw new InvalidOperationException(ResultCode.MarkerIconShaderUnavailable);
            // The icon stands on the sign's backdrop: its own queue keeps it over the backdrop from every angle.
            entry.IconMaterial = new Material(shader) { renderQueue = WorldBackdrop.PictureQueue };
            for (int i = 0; i < sources.Count; i++)
            {
                var source = sources[i];
                if (source == null || source.sprite == null) continue;
                var obj = new GameObject("icon"); obj.transform.SetParent(entry.RootTransform, false);
                var renderer = obj.AddComponent<SpriteRenderer>();
                renderer.sprite = source.sprite; renderer.color = color; renderer.flipX = source.flipX; renderer.flipY = source.flipY;
                renderer.sharedMaterial = entry.IconMaterial;
                obj.transform.localPosition = holder.InverseTransformPoint(source.transform.position) * (1f / extent);
                obj.transform.localRotation = Quaternion.Inverse(holder.rotation) * source.transform.rotation;
                var s = source.transform.lossyScale;
                obj.transform.localScale = new Vector3(s.x / basis.x, s.y / basis.y, s.z / basis.z) * (1f / extent);
            }
        }
        finally { ForgeCore.NavMarkers.Remove(sample); }
    }

    // The quad sits behind the text and icon, inside the label root, so the author's size and facing carry it with them.
    private static void CreateBackground(Entry entry, Color colour)
    {
        entry.Backdrop = WorldBackdrop.TryCreate(entry.RootTransform, "background", colour)
            ?? throw new InvalidOperationException(ResultCode.MarkerBackgroundShaderUnavailable);
        LayoutBackground(entry);
    }

    /// <summary>Fits the backdrop around what the label shows: the words, the icon, or both side by side.</summary>
    private static void LayoutBackground(Entry entry)
    {
        if (entry.Backdrop == null) return;
        var padding = entry.BackdropPadding;
        float left, right, bottom, top;
        bool icon = entry.IconMaterial != null;
        if (entry.Text == null) { left = bottom = -.5f; right = top = .5f; }
        else if (icon)
        {
            left = -.5f; right = .75f + entry.TextSize.x;
            bottom = Math.Min(0, entry.TextSize.y / 2 - .5f); top = Math.Max(entry.TextSize.y, entry.TextSize.y / 2 + .5f);
        }
        else { left = -entry.TextSize.x / 2; right = entry.TextSize.x / 2; bottom = 0; top = entry.TextSize.y; }
        // Positive local Z is behind the text plane.
        entry.Backdrop.Place(new Vector2(right - left + padding * 2, top - bottom + padding * 2), new Vector2((left + right) / 2, (bottom + top) / 2));
    }

    internal static GameObject? PlacedItem(ItemReplicator replicator)
    {
        var placed = replicator.Item?.TryCast<ItemInLevel>();
        var sync = placed?.GetSyncComponent()?.TryCast<LG_PickupItem_Sync>();
        return sync != null && sync.GetCurrentState().status == ePickupItemStatus.PlacedInLevel ? placed!.gameObject : null;
    }

    private static GameObject? PlacedItem(LG_PickupItem_Sync sync)
        => sync.item is { WasCollected: false } item && item != null
            && sync.GetCurrentState().status == ePickupItemStatus.PlacedInLevel ? item.gameObject : null;
    private static bool Same(Vector3 a, Vector3 b) => a.x == b.x && a.y == b.y && a.z == b.z;
    private static bool Same(Quaternion a, Quaternion b) => a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w;

    /// <summary>Why a display is no longer live, or null while it is. Each lost object has its own reason, so a removal
    /// the world itself caused (a destroyed root, a new world) is never recorded as a lost receiver.</summary>
    private static string? LostReason(Entry entry, long epoch)
        => LostReason(entry, epoch, MapHost.Session?.MapObjects?.InteractionPoints);

    private static string? LostReason(Entry entry, long epoch, InteractionPointSource? points)
    {
        if (entry.Target.WorldEpoch != epoch) return "world-replaced";
        if (entry.Root.WasCollected || entry.Root == null || entry.RootTransform.WasCollected || entry.RootTransform == null) return "root-destroyed";
        if (entry.CameraFacing && (ReferenceEquals(entry.Camera, null) || entry.Camera.WasCollected || entry.Camera == null)) return "camera-lost";
        if (entry.Zone != null && !entry.Zone.Placed) return "zone-lost";
        if (!ReferenceEquals(entry.ScopeZone, null) && (entry.ScopeZone.WasCollected || entry.ScopeZone == null)) return "zone-lost";
        if (entry.Point != null && !ReferenceEquals(points?.ById(entry.Point.Id), entry.Point)) return "point-lost";
        // ReferenceEquals distinguishes an absent receiver (world point) from Unity's destroyed-object null.
        if (!ReferenceEquals(entry.Receiver, null) && (entry.Receiver.WasCollected || entry.Receiver == null)) return "receiver-lost";
        if (!ReferenceEquals(entry.Owner, null) && (entry.Owner.WasCollected || entry.Owner == null)) return "owner-lost";
        // An item leaving the floor is the item's own state change (ItemLeftLevel); this sweep runs every frame and
        // never reads the pickup state, which builds a native state wrapper on each read.
        if (entry.Item != null && entry.Item.WasCollected) return "item-moved";
        return null;
    }

    /// <summary>An item left the level floor, from its own replicated state change: every label hanging on it goes.</summary>
    internal static void ItemLeftLevel(LG_PickupItem_Sync sync)
    {
        if (sync.item is not { WasCollected: false } item || item == null) return;
        var pointer = item.gameObject.Pointer;
        for (int i = Entries.Count - 1; i >= 0; i--)
        {
            var entry = Entries[i];
            if (entry.Item == null && entry.Owner is not LG_PickupItem_Sync) continue;
            if (entry.Receiver is not { } receiver || receiver.WasCollected || receiver.Pointer != pointer) continue;
            Entries.RemoveAt(i); Release(entry, "item-moved");
        }
    }

    internal static void Tick(long epoch)
    {
        if (Entries.Count == 0) return;
        bool languageRead = false, languageAvailable = false;
        Localization.Language language = default;
        string languageCode = "";
        var points = MapHost.Session?.MapObjects?.InteractionPoints;
        float now = ForgeCore.Frame.GameTime;
        for (int i = Entries.Count - 1; i >= 0; i--)
        {
            var entry = Entries[i];
            if (LostReason(entry, epoch, points) is { } reason)
            { Entries.RemoveAt(i); Release(entry, reason); continue; }
            if (entry.ExpiresAt > 0 && now >= entry.ExpiresAt)
            { Entries.RemoveAt(i); Release(entry, "expired"); continue; }
            var root = entry.RootTransform!;
            if (entry.UsesPlayerLanguage)
            {
                if (!languageRead)
                { languageAvailable = StationTextValue.TryCurrentLanguage(out language, out languageCode); languageRead = true; }
                if (!languageAvailable)
                {
                    if (entry.LanguageFailure != languageCode)
                    { entry.LanguageFailure = languageCode; Result(entry.Target, entry.Hud, false, languageCode); }
                    continue;
                }
                entry.LanguageFailure = null;
                if (!entry.TextLanguageKnown || entry.TextLanguage != language)
                {
                    entry.TextLanguage = language;
                    entry.TextLanguageKnown = true;
                    if (!StationTextValue.TryRender(entry.TextReference, language, out var translated))
                    { Entries.RemoveAt(i); Release(entry, "marker-text-unavailable"); continue; }
                    if (entry.Hud && !SingleLine(translated))
                    { Entries.RemoveAt(i); Release(entry, "marker-text-must-be-single-line"); continue; }
                    if (translated != entry.RenderedText)
                    {
                        entry.RenderedText = translated;
                        if (entry.Text != null)
                        {
                            LayoutText(entry, translated);
                            StationSignPlacementReadback.Record(epoch, entry.Target, entry.Point, entry.Owner, root, entry.Text);
                        }
                        if (entry.Marker != null)
                        {
                            entry.Marker.m_title.SetText(translated);
                            entry.Marker.m_title.rectTransform.sizeDelta = entry.Marker.m_title.GetPreferredValues(translated);
                        }
                        TellRecording(entry);
                    }
                }
            }
            var position = entry.Zone is { } zone
                ? new Vector3((float)zone.Position[0], (float)(zone.Position[1] + zone.TopHeight()), (float)zone.Position[2]) + entry.Offset
                : entry.Receiver != null ? entry.ReceiverTransform!.position + entry.Offset : entry.WrittenPosition;
            if (!ForgeCore.Vec.Finite(position))
            { Entries.RemoveAt(i); Release(entry, "position-invalid"); continue; }
            if (!Same(entry.WrittenPosition, position))
            { root.position = position; entry.WrittenPosition = position; TellRecording(entry); }
        }
    }
    /// <summary>Restyles the world sign shown on <paramref name="target"/>: null when the target shows none, so the
    /// caller can ask the next display table; otherwise the result code. A new text keeps following the player's
    /// language exactly as a sign shown with it would.</summary>
    internal static string? Restyle(EntityReference target, TextRestyle restyle)
    {
        if (ShownSign(target) is not { } entry) return null;
        if (restyle.Text is { } text)
        {
            if (entry.Body == null) return ResultCode.TextStyleNotShown;
            bool usesPlayerLanguage = StationTextValue.RequiresLanguage(text);
            Localization.Language language = default;
            if (usesPlayerLanguage)
            {
                try { language = StationTextValue.CurrentLanguage; }
                catch (RuntimeContractException error) { return error.Code; }
            }
            if (!(usesPlayerLanguage ? StationTextValue.TryRender(text, language, out var shown) : StationTextValue.TryRender(text, out shown)))
                return ResultCode.TextStyleInvalid;
            if (shown.Length == 0) return ResultCode.TextStyleEmpty;
            entry.TextReference = text; entry.UsesPlayerLanguage = usesPlayerLanguage;
            entry.TextLanguage = language; entry.TextLanguageKnown = usesPlayerLanguage; entry.RenderedText = shown;
            LayoutText(entry, shown);
        }
        if (restyle.Color is { } color)
        {
            if (entry.Text != null) entry.Text.color = color;
            entry.TextColor = color;
            for (int i = 0; i < entry.RootTransform.childCount; i++)
                if (entry.RootTransform.GetChild(i).gameObject.name == "icon"
                    && entry.RootTransform.GetChild(i).GetComponent<SpriteRenderer>() is { } icon) icon.color = color;
        }
        if (restyle.Background != null || restyle.Opacity != null)
        {
            var current = entry.BackdropColor;
            var next = restyle.Background is { } rgb ? new Color(rgb.r, rgb.g, rgb.b, current.a) : current;
            if (restyle.Opacity is { } opacity) next.a = opacity;
            entry.BackdropColor = next;
            if (next.a <= 0) { entry.Backdrop?.Destroy(); entry.Backdrop = null; }
            else if (entry.Backdrop == null) CreateBackground(entry, next);
            else entry.Backdrop.SetColor(next);
        }
        if (entry.Text != null) StationSignPlacementReadback.Record(entry.Target.WorldEpoch, entry.Target, entry.Point, entry.Owner, entry.RootTransform, entry.Text);
        TellRecording(entry);
        return TextStyleActions.AppliedCode;
    }

    private static Entry? ShownSign(EntityReference target)
    {
        for (int i = 0; i < Entries.Count; i++)
            if (!Entries[i].Hud && Entries[i].Target == target) return Entries[i];
        return null;
    }

    /// <summary>The native text and backdrop of the sign shown on <paramref name="target"/>; null when it shows none.</summary>
    internal static (TextMeshPro? Text, WorldBackdrop? Backdrop)? InspectTextLook(EntityReference target)
        => ShownSign(target) is { } entry ? (entry.Text, entry.Backdrop) : null;

    private static bool SingleLine(string text) => !text.Contains('\r') && !text.Contains('\n') && !text.Contains('\u2028') && !text.Contains('\u2029');
    private static void LayoutText(Entry entry, string text)
    {
        // Same as placed-model text: the layout width stays fixed so TMP keeps this wrap; preferred bounds place the icon.
        var lineWidth = (float)PlacedModelTextStyle.DefaultLineWidth;
        var preferred = entry.Body!.SetContent(text, lineWidth);
        var size = new Vector2(Math.Min(lineWidth, preferred.x), preferred.y);
        entry.Body.SetLocalPosition(new(entry.IconMaterial == null ? 0 : 0.75f + size.x / 2, 0, 0));
        // Keep each native sprite's relative placement, and centre the entire icon on the translated board.
        for (int i = 0; i < entry.RootTransform.childCount; i++)
        {
            var child = entry.RootTransform.GetChild(i);
            if (child.gameObject.name == "icon") child.localPosition += new Vector3(0, (size.y - entry.TextSize.y) / 2, 0);
        }
        entry.TextSize = size;
        LayoutBackground(entry);
    }
    private static void Remove(EntityReference target, bool hud, string reason)
    {
        for (int i = Entries.Count - 1; i >= 0; i--) if (Entries[i].Target == target && Entries[i].Hud == hud)
        { var entry = Entries[i]; Entries.RemoveAt(i); Release(entry, reason); }
    }
    private static void RemoveAll(EntityReference target)
    { Remove(target, false, "receiver-lost"); Remove(target, true, "receiver-lost"); }
    internal static void Clear(string reason = "world-replaced")
    {
        for (int i = Entries.Count - 1; i >= 0; i--) Release(Entries[i], reason);
        Entries.Clear();
        ClearReplayed();
        foreach (var (key, count) in RecordCounts)
            if (count > RepeatedRecordLimit)
                MapLog.Record(RuntimeLogLevel.Info, key.Hud ? "map.hud-marker" : "map.world-label", "Map display updates repeated.",
                    RuntimeLogValue.Of("entity", key.Entity), RuntimeLogValue.Of("phase", key.Phase), RuntimeLogValue.Of("reason", key.Reason),
                    RuntimeLogValue.Of("repeated", (long)(count - RepeatedRecordLimit)));
        RecordCounts.Clear();
    }
    internal static void ClearWorldLabels(string reason)
    {
        for (int i = Entries.Count - 1; i >= 0; i--)
            if (!Entries[i].Hud)
            { var entry = Entries[i]; Entries.RemoveAt(i); Release(entry, reason); }
    }
    private static void Release(Entry entry, string reason)
    {
        Discard(entry);
        // A replacement keeps the display in its `shown` phase; the new entry's own release reports the removal.
        if (reason == "replaced") return;
        Record(entry, "removed", reason);
        if (!entry.Hud) ForgeCore.Recording.State(RecordingKey(entry), null);
    }

    private static void Discard(Entry entry)
    {
        // A wrapper whose native object was collected throws on any member or `!= null` comparison, so every owned
        // object is checked with WasCollected (a reference pattern, no Unity comparison) before it is touched.
        if (entry.Marker is { WasCollected: false } marker && marker != null) ForgeCore.NavMarkers.Remove(marker);
        if (entry.Root is { WasCollected: false } root && root != null) Object.Destroy(root);
        if (entry.IconMaterial is { WasCollected: false } icon && icon != null) Object.Destroy(icon);
        entry.Body?.Destroy();
        entry.Backdrop?.Destroy();
    }
    private static void Record(Entry entry, string phase, string reason)
    {
        var key = new RecordKey(entry.Hud, entry.Target.Id, phase, reason);
        RecordCounts.TryGetValue(key, out var seen);
        RecordCounts[key] = ++seen;
        if (seen > RepeatedRecordLimit) return;
        MapLog.Record(RuntimeLogLevel.Info,
            entry.Hud ? "map.hud-marker" : "map.world-label", "Map display updated.",
            RuntimeLogValue.Of("entity", entry.Target.Id), RuntimeLogValue.Of("phase", phase), RuntimeLogValue.Of("reason", reason));
    }
    /// <summary>The answer for one display: a shown one commits no world state (`partial + none`, as every
    /// presentation row), a refused one is logged and rejected.</summary>
    private static Outcome Result(EntityReference target, bool hud, bool shown, string code)
    {
        if (shown) return new Outcome(Status.Partial, Commit.None, code);
        MapLog.Record(RuntimeLogLevel.Error, hud ? "map.hud-marker-refused" : "map.world-label-refused",
            "Map display refused.", RuntimeLogValue.Of("entity", target.Id), RuntimeLogValue.Of("reason", code));
        return Outcome.Rejected(code);
    }

}
