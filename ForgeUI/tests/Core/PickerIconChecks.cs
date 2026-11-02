using CellMenu;
using ForgeUI;
using UnityEngine;

/// <summary>The picker's pictures (list rows and the details box) are drawn by the picker's own skin inside their own frames,
/// whatever the game's placeholder picture looks like or where the box's collider sits, and above the game's own pieces.
/// A frame that put the picture outside the window and a placeholder that left it a dot were the two ways it went wrong in game.</summary>
internal static class PickerIconChecks
{
    private sealed class IconSource : PickerSource
    {
        public readonly Sprite Picture = Sprite(2.56f, 1f);
        public override int SlotCount => 1;
        public override PickerCard SlotCard(int slot, bool chosen) => new() { Title = "HELD" };
        public override IReadOnlyList<PickerCard> Entries(int tab) => new[] { new PickerCard { Title = "A", Icon = Picture }, new PickerCard { Title = "B", Icon = Picture } };
        public override string? Pick(int slot, int tab, int entry) => null;
        public override PickerInfo Info(int slot, int tab, int entry) => new() { Title = "A", Icon = Picture };
    }

    private static Sprite Sprite(float w, float h) => new() { bounds = new Bounds { min = new Vector3(-w / 2f, -h / 2f, 0f), max = new Vector3(w / 2f, h / 2f, 0f) } };

    /// <summary>A card with a placeholder picture of a tiny sprite off to one side, as the game's prefab has.</summary>
    private static GameObject Card()
    {
        var go = UiChecks.ButtonPrefab();
        var card = go.GetComponent<CM_LobbyScrollItem>()!;
        card.m_collider.size = new Vector2(800f, 120f);
        card.m_icon = UiChecks.Child<SpriteRenderer>(go.transform, "Icon");
        card.m_icon.sprite = Sprite(0.01f, 0.01f);
        card.m_icon.sortingOrder = 7;
        card.m_icon.transform.localPosition = new Vector3(-150f, 0f, 0f);
        go.Factory = Card;
        return go;
    }

    /// <summary>The picture's rectangle: its sprite bounds through its own transform.</summary>
    private static (float Left, float Bottom, float Right, float Top) Shown(SpriteRenderer icon)
    {
        var size = icon.sprite!.bounds.size;
        var a = icon.transform.TransformPoint(new Vector3(-size.x / 2f, -size.y / 2f, 0f));
        var b = icon.transform.TransformPoint(new Vector3(size.x / 2f, size.y / 2f, 0f));
        return (a.x, a.y, b.x, b.y);
    }

    private static (float Left, float Bottom, float Right, float Top) Frame(BoxCollider2D box)
    {
        var a = box.transform.TransformPoint(new Vector3(box.offset.x - box.size.x / 2f, box.offset.y - box.size.y / 2f, 0f));
        var b = box.transform.TransformPoint(new Vector3(box.offset.x + box.size.x / 2f, box.offset.y + box.size.y / 2f, 0f));
        return (a.x, a.y, b.x, b.y);
    }

    private static bool Inside((float Left, float Bottom, float Right, float Top) icon, (float Left, float Bottom, float Right, float Top) frame) =>
        icon.Left >= frame.Left - 0.01f && icon.Right <= frame.Right + 0.01f && icon.Bottom >= frame.Bottom - 0.01f && icon.Top <= frame.Top + 0.01f;

    private static float Width((float Left, float Bottom, float Right, float Top) r) => r.Right - r.Left;

    /// <summary>The shown copies of <paramref name="picture"/> under <paramref name="root"/>.</summary>
    private static SpriteRenderer[] Drawn(Component root, Sprite picture) =>
        root.GetComponentsInChildren<SpriteRenderer>().Where(s => ReferenceEquals(s.sprite, picture)).ToArray();

    internal static void Run(Action<bool, string> check)
    {
        var bar = UiChecks.Lobby();
        bar.m_scrollWindowCardPrefab = Card();
        var window = bar.m_popupScrollWindow;
        // The details box keeps its collider on an object away from the box's own origin, and its picture on another.
        var box = UiChecks.Child<CM_ScrollWindowInfoBox>(window.transform, "Info");
        var frameObject = UiChecks.Child<Component>(box.transform, "Frame");
        frameObject.transform.localPosition = new Vector3(700f, -300f, 0f);
        box.m_collider = frameObject.gameObject.AddComponent<BoxCollider2D>();
        box.m_collider.size = new Vector2(520f, 630f);
        box.m_infoMainIcon = UiChecks.Child<SpriteRenderer>(frameObject.transform, "MainIcon");
        box.m_infoMainIcon.sprite = Sprite(0.01f, 0.01f);
        box.m_infoDescriptionText = UiChecks.Child<TMPro.TextMeshPro>(frameObject.transform, "Description");
        box.m_infoDescriptionText.gameObject.Factory = () => { var copy = new GameObject(); copy.AddComponent<TMPro.TextMeshPro>(); return copy; };
        window.InfoBox = box;

        var source = new IconSource();
        Picker.Open(bar, source);
        var cards = window.GetComponentsInChildren<CM_LobbyScrollItem>(true).Where(c => c.gameObject.name.StartsWith("ForgeUI.Entry")).ToArray();
        var rows = cards.Select(c => (Card: c, Pictures: Drawn(c, source.Picture), Frame: Frame(c.m_collider))).ToArray();
        check(rows.Length == 2 && rows.All(r => r.Pictures.Length == 1 && Inside(Shown(r.Pictures[0]), r.Frame) && Width(Shown(r.Pictures[0])) > 40f),
            "a row picture is shown whole and large inside its own row, though the game's placeholder is a dot");
        check(rows.All(r => r.Pictures[0].sortingOrder > r.Card.m_icon!.sortingOrder), "a row picture is drawn above the card's own pieces");
        var info = Drawn(box, source.Picture);
        check(info.Length == 1 && Inside(Shown(info[0]), Frame(box.m_collider)) && Width(Shown(info[0])) > 40f && !box.m_infoMainIcon.enabled,
            "the details picture is drawn inside the details frame (its collider is on another object than the box), the game's own picture off");

        // Redrawn every hover and tick: the same pieces stay where they are.
        var before = rows.Select(r => Shown(r.Pictures[0])).ToArray();
        Picker.Tick();
        Picker.Tick();
        check(rows.Select(r => Drawn(r.Card, source.Picture)).All(p => p.Length == 1) && rows.Select(r => Shown(r.Pictures[0])).SequenceEqual(before),
            "row pictures keep their place and size from frame to frame, no piece added");
        UIKit.ClosePicker();
    }
}
