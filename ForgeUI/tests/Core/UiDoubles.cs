// The tests link production UI orchestration; these doubles provide a small translated/scaled scene and native lists.
// Rendering, Unity destruction semantics, anchoring and physics still require the in-game checks in VALIDATION.md.
namespace UnityEngine
{
    public class Object : Il2CppSystem.Object
    {
        public bool Destroyed;
        public virtual bool WasCollected => Destroyed;
        public static T[] FindObjectsOfType<T>() where T : Object => System.Array.Empty<T>();
        public static GameObject Instantiate(GameObject source, Transform parent)
        {
            var copy = source.Factory!();
            copy.transform.SetParent(parent, false);
            return copy;
        }
        public static void Destroy(Object obj)
        {
            obj.Destroyed = true;
            if (obj is GameObject go) go.SetActive(false);
        }
        public static void DestroyImmediate(Object obj)
        {
            obj.Destroyed = true;
            if (obj is Component component) component.gameObject.Remove(component);
            if (obj is GameObject go) go.SetActive(false);
        }
    }
    public class Behaviour : Component { public bool enabled = true; }
    public class MonoBehaviour : Behaviour { }
    public class Camera : Behaviour
    {
        public int cullingMask = -1;
        /// <summary>The test's projection: world x to viewport x is (x - Origin) / Width.</summary>
        public float Origin, Width = 1920f;
        public Vector3 WorldToViewportPoint(Vector3 p) => new((p.x - Origin) / Width, 0.5f, 1f);
        public Vector3 ViewportToWorldPoint(Vector3 v) => new(v.x * Width + Origin, 0f, 0f);
    }
    public class Component : Object
    {
        public GameObject gameObject = null!;
        public Transform transform => gameObject.transform;
        public override bool WasCollected => Destroyed || (gameObject?.Destroyed ?? false);
        public T? GetComponent<T>() where T : Component => gameObject.GetComponent<T>();
        public T[] GetComponentsInChildren<T>(bool inactive = false) where T : Component => gameObject.GetComponentsInChildren<T>(inactive);
        public T? GetComponentInChildren<T>(bool inactive = false) where T : Component => gameObject.GetComponentsInChildren<T>(inactive).FirstOrDefault();
    }
    public class GameObject : Object
    {
        public string name;
        public int layer;
        public bool activeSelf = true;
        public bool activeInHierarchy => activeSelf && (transform.parent?.gameObject.activeInHierarchy ?? true);
        public readonly RectTransform transform;
        public Func<GameObject>? Factory;
        private readonly List<Component> _components = new();
        public GameObject(string name = "") { this.name = name; transform = new RectTransform { gameObject = this }; }
        public void SetActive(bool active) => activeSelf = active;
        public T AddComponent<T>() where T : Component, new()
        {
            var item = new T { gameObject = this };
            _components.Add(item);
            return item;
        }
        public T? GetComponent<T>() where T : Component => _components.OfType<T>().FirstOrDefault();
        public T[] GetComponents<T>() where T : Component => _components.OfType<T>().ToArray();
        public void Remove(Component component) => _components.Remove(component);
        public T[] GetComponentsInChildren<T>(bool inactive = false) where T : Component =>
            (!inactive && !activeInHierarchy ? Enumerable.Empty<T>() : _components.OfType<T>())
                .Concat(transform.Children.SelectMany(t => t.gameObject.GetComponentsInChildren<T>(inactive))).ToArray();
    }
    public class Transform : Component
    {
        public Transform parent = null!;
        internal readonly List<Transform> Children = new();
        public Vector3 localPosition, localScale = Vector3.one;
        public Quaternion localRotation;
        public Vector3 lossyScale => parent == null ? localScale : Vector3.Multiply(parent.lossyScale, localScale);
        public Vector3 position { get => parent == null ? localPosition : parent.TransformPoint(localPosition); set => localPosition = parent == null ? value : parent.InverseTransformPoint(value); }
        public int childCount => Children.Count;
        public Transform GetChild(int i) => Children[i];
        public void SetParent(Transform value, bool worldPositionStays) { parent?.Children.Remove(this); parent = value; value.Children.Add(this); }
        public bool IsChildOf(Transform other) => this == other || (parent?.IsChildOf(other) ?? false);
        public Vector3 TransformVector(Vector3 v) => Vector3.Multiply(v, lossyScale);
        public Vector3 InverseTransformVector(Vector3 v) => Vector3.Divide(v, lossyScale);
        public Vector3 TransformPoint(Vector3 v) => position + TransformVector(v);
        public Vector3 InverseTransformPoint(Vector3 v) => InverseTransformVector(v - position);
        public Matrix4x4 worldToLocalMatrix { get { var s = lossyScale; var p = position; return new Matrix4x4 { m00 = 1 / s.x, m03 = -p.x / s.x, m11 = 1 / s.y, m13 = -p.y / s.y }; } }
    }
    public struct Matrix4x4 { public float m00, m01, m02, m03, m10, m11, m12, m13; }
    public class Material : Object { }
    public class MeshRenderer : Renderer { }
    public class RectTransform : Transform { public Vector2 pivot, sizeDelta; public Rect rect => new(0f, 0f, sizeDelta.x, sizeDelta.y); }
    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public static Vector2 operator -(Vector2 a, Vector2 b) => new(a.x - b.x, a.y - b.y);
        public static Vector2 operator /(Vector2 a, float b) => new(a.x / b, a.y / b);
    }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero => new(0, 0, 0);
        public static Vector3 one => new(1, 1, 1);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator *(Vector3 a, float b) => new(a.x * b, a.y * b, a.z * b);
        public static Vector3 Multiply(Vector3 a, Vector3 b) => new(a.x * b.x, a.y * b.y, a.z * b.z);
        public static Vector3 Divide(Vector3 a, Vector3 b) => new(a.x / b.x, a.y / b.y, a.z / b.z);
        public static bool operator ==(Vector3 a, Vector3 b) => a.x == b.x && a.y == b.y && a.z == b.z;
        public static bool operator !=(Vector3 a, Vector3 b) => !(a == b);
        public override bool Equals(object? obj) => obj is Vector3 v && this == v;
        public override int GetHashCode() => HashCode.Combine(x, y, z);
    }
    public struct Quaternion { public static Quaternion identity => default; public static Quaternion Euler(float x, float y, float z) => default; }
    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public static Color white => new(1f, 1f, 1f, 1f);
        public static bool operator ==(Color x, Color y) => x.r == y.r && x.g == y.g && x.b == y.b && x.a == y.a;
        public static bool operator !=(Color x, Color y) => !(x == y);
        public override bool Equals(object? obj) => obj is Color c && this == c;
        public override int GetHashCode() => HashCode.Combine(r, g, b, a);
    }
    public struct Rect
    {
        private readonly Vector2 _offset, _size;
        public Rect(float x, float y, float width, float height) { _offset = new(x, y); _size = new(width, height); }
        public Rect(Vector2 offset, Vector2 size) { _offset = offset; _size = size; }
        public static Rect MinMaxRect(float xMin, float yMin, float xMax, float yMax) => new(xMin, yMin, xMax - xMin, yMax - yMin);
        public float xMin => _offset.x;
        public float yMin => _offset.y;
        public float xMax => _offset.x + _size.x;
        public float yMax => _offset.y + _size.y;
        public float width => _size.x;
        public float height => _size.y;
        public Vector2 center => new(_offset.x + width / 2, _offset.y + height / 2);
    }
    public struct Bounds { public Vector3 min, max; public Vector3 size => max - min; public Vector3 center => (min + max) * 0.5f; }
    public class Renderer : Component
    {
        public bool enabled = true, isVisible, forceRenderingOff;
        public int sortingLayerID, sortingOrder;
        public Vector2 Size = new(1, 1);
        public Bounds bounds => new() { min = transform.TransformPoint(new(-Size.x / 2, -Size.y / 2, 0)), max = transform.TransformPoint(new(Size.x / 2, Size.y / 2, 0)) };
    }
    public enum SpriteDrawMode { Simple, Sliced, Tiled }
    public class Sprite : Object
    {
        public Bounds bounds;
        public static Sprite Create(Texture2D texture, Rect rect, Vector2 pivot, float pixelsPerUnit) => new();
    }
    public class Texture2D : Object { public int width = 4, height = 4; public static Texture2D whiteTexture { get; } = new(); }
    public class SpriteRenderer : Renderer
    {
        public Sprite? sprite;
        public SpriteDrawMode drawMode;
        public Vector2 size;
        public Material? sharedMaterial;
                public Color color;
    }
    public class Collider2D : Component
    {
        public bool enabled = true;
        public Vector2 offset, size;
        public Bounds bounds => new() { min = transform.TransformPoint(new(offset.x - size.x / 2, offset.y - size.y / 2, 0)), max = transform.TransformPoint(new(offset.x + size.x / 2, offset.y + size.y / 2, 0)) };
    }
    public class BoxCollider2D : Collider2D { }
    public static class Screen { public static int width = 2560, height = 1440; }
    public static class Time { public static int frameCount; public static float time; public static float realtimeSinceStartup = 100f; }
}
namespace UnityEngine.Events { public class UnityEvent { } }
namespace TMPro
{
    public enum TextAlignmentOptions { Left, MidlineLeft, MidlineRight, TopLeft, Midline }
    public class TMP_FontAsset { public readonly HashSet<char> Chars = new(); public bool HasCharacter(char c, bool fallbacks, bool add) => Chars.Contains(c); }
    public enum TextOverflowModes { Overflow, Ellipsis }
    public class TextMeshPro : UnityEngine.Renderer
    {
        public bool richText, enableWordWrapping, enableAutoSizing, isTextOverflowing;
        public TextOverflowModes overflowMode;
        /// <summary>As TMP does for glyphs its font lacks: the first glyph past ASCII is drawn by a fallback-font sub-mesh, a child object
        /// the text makes under itself when it builds its mesh.</summary>
        public void ForceMeshUpdate(bool ignoreActiveState = false, bool forceTextReparsing = false)
        {
            if (transform.childCount > 0 || text.All(c => c < 128)) return;
            var sub = new UnityEngine.GameObject("TMP SubMesh");
            sub.transform.SetParent(transform, false);
            sub.AddComponent<UnityEngine.MeshRenderer>();
        }
        public float fontSize = 30, fontSizeMin, fontSizeMax, alpha;
        public string text = "";
        public TMP_FontAsset? font;
        public UnityEngine.Material? fontSharedMaterial;
        public void SetText(string value) => text = value;
        /// <summary>Offline text measure: each character is half the font size wide, one line is the font size high.</summary>
        public UnityEngine.Vector2 GetPreferredValues(string value, float width, float height) => new(value.Length * fontSize * 0.5f, fontSize);
        public TextAlignmentOptions alignment;
        public UnityEngine.RectTransform rectTransform => (UnityEngine.RectTransform)transform;
        public UnityEngine.Color color;
    }
}
namespace Il2CppSystem.Collections.Generic
{
    public class List<T> : System.Collections.Generic.List<T> { }
    public class Dictionary<TKey, TValue> : System.Collections.Generic.Dictionary<TKey, TValue> where TKey : notnull { }
}
namespace ForgeUI
{
    /// <summary>The Harmony patches are not put on offline; the groups asked for are recorded.</summary>
    internal static class PatchGroups
    {
        internal static readonly System.Collections.Generic.List<PatchGroup> Ensured = new();
        internal static bool Has(PatchGroup group) => Ensured.Contains(group);
        internal static void Ensure(PatchGroup group) { if (!Has(group)) Ensured.Add(group); }
    }

    /// <summary>The font bundle is not there offline: every text uses the game's font.</summary>
    internal static class UIKitFonts
    {
        internal sealed class Set { internal TMPro.TMP_FontAsset Regular = null!, Bold = null!; }
        internal static Set? For(TMPro.TMP_FontAsset game, UnityEngine.Material material) => null;
    }
}
public enum GuiAnchor { TopLeft }
namespace CellMenu
{
    using UnityEngine;
    public enum eCM_MenuPage { CMP_NONE, CMP_RUNDOWN_NEW, CMP_SETTINGS }
    public class iScrollWindowContent : Il2CppSystem.Object { public iScrollWindowContent(IntPtr pointer) { Pointer = pointer; } }
    public class iCellMenuPopupController { public iCellMenuPopupController(IntPtr pointer) { } }
    public class CM_PageBase : Component
    {
        public readonly List<Il2CppSystem.Object> m_clickAnywhereListeners = new();
        public Transform? m_resolutionHolder;
        public Component? m_cursor;
    }
    /// <summary>The settings page: its sections, windows and resolution holder under the moving content, as the game builds them.</summary>
    public class CM_PageSettings : CM_PageBase
    {
        public Transform? m_movingContentHolder;
        public Il2CppSystem.Collections.Generic.List<CM_ScrollWindow>? m_allSettingsWindows;
    }
    public class CM_SettingsItem : CM_Item
    {
        public Transform? m_inputAlign;
        public SpriteRenderer? m_background;
        public TMPro.TextMeshPro? m_title;
        public string Title = "";
        public Vector2 Size;
        public void SetSize(Vector2 size) => Size = size;
        public override void SetText(string text) => Title = text;
    }
    public class CM_TimedButton : CM_Item { }
    public class TooltipInfo { public bool UseTooptip; public string TooltipHeader = "", TooltipText = ""; }
    public class MainMenuGuiLayer
    {
        public eCM_MenuPage m_currentPageEnum;
        public readonly Dictionary<eCM_MenuPage, CM_PageBase> Pages = new();
        public CM_PageBase? GetPage(eCM_MenuPage page) => Pages.TryGetValue(page, out var p) ? p : null;
        public void ChangePage(eCM_MenuPage page)
        {
            ForgeUI.MenuScreens.PageChanging();
            foreach (var (key, value) in Pages) value.gameObject.SetActive(key == page);
            m_currentPageEnum = page;
        }
    }
    public class CM_PageLoadout : CM_PageBase
    {
        public static CM_PageLoadout? Current;
        public CM_PlayerLobbyBar[]? m_playerLobbyBars;
        public Transform? m_movingContentHolder;
        public Vector2 m_localHolderOffset, m_holderMoveLeftRight;
    }
    public class CM_PlayerLobbyBar : Component
    {
        public const float POPUP_WIDTH = 1600, POPUP_HEIGHT = 760, WEAPON_INFOBOX_WIDTH = 600;
        public CM_LobbyScrollItem m_clothesButton = null!;
        public CM_ScrollWindow m_popupScrollWindow = null!;
        public GameObject m_scrollWindowCardPrefab = null!, m_popupInfoBoxWeaponPrefab = null!;
        public Player? m_player;
        public class Player { public bool IsLocal = true; }
        public void ShowPopup() => m_popupScrollWindow.SetVisible(true);
        public void HidePopup() => m_popupScrollWindow.SetVisible(false);
        public void ShowWeaponSelectionPopup() { }
        public void ShowClothesSelect() { }
        public void ShowBoosterImplantTeamView() { }
        public void ShowBoosterImplantSelect() { }
    }
    public class CM_MenuBarItem : CM_Item
    {
        public bool m_alphaTextOnHover;
        public TMPro.TextMeshPro? m_text;
        public override void SetText(string text) { m_text!.text = text; }
        public void CorrectSizeToText(float min, float max) => m_collider.size = new(Math.Min(max, m_text!.text.Length * 20), 55);
        public Vector2 GetSize() => m_collider.size;
        public void SetAnchor(GuiAnchor anchor, bool value) { }
        public void Setup(object root, eCM_MenuPage page) { }
        public void SetXOffset(float x) => transform.localPosition = new(x, 0, 0);
    }
    public class CM_MenuBar : Component
    {
        public float m_buttonLeftSpacing = 280;
        public CM_MenuBarItem? m_settings, m_rundown, m_loadout, m_exitLobbyAndExpedition, m_returnToLobby, m_quitGame;
        public Component? m_expIcon;
        public TMPro.TextMeshPro? m_expText;
        public Transform? m_statusAlign;
        public GameObject m_menuBarItemPrefab = null!, m_popupWindowPrefab = null!;
        public object m_root = new();
        public CM_PageBase m_basePage = null!;
        public void Setup() { }
        public void UpdateMenuOptions() { }
        public void SetPopupVisible(bool value) { }
    }
    public class CM_ScrollWindow : CM_Item
    {
        public bool hasInfoBox;
        public float m_infoBoxWidth;
        public SpriteRenderer m_background = null!, m_outline = null!;
        public Component m_contentContainer = null!;
        public Vector3 m_originalContentPosition;
        public CM_PageBase m_pageBase = null!;
        public CM_ScrollWindowInfoBox? InfoBox;
        public CM_ScrollWindowHeader[]? m_headers;
        public UIClipFeeder? m_uiClipFeeder;
        // Made by the page's own set-up, not saved with the window: a copy has none (see MenuScreenChecks).
        public Il2CppSystem.Collections.Generic.List<iScrollWindowContent>? m_contentItems = new();
        public Il2CppSystem.Collections.Generic.List<CM_Item>? m_nonContentItems = new();
        public Il2CppSystem.Collections.Generic.Dictionary<int, int>? m_headersDict = new();
        public Transform? m_root;
        public CM_Item[] PopupChildItems = Array.Empty<CM_Item>();
        public void Setup(object root) { }
        // The game's set-up makes the content lists a copy lacks.
        public new void Setup() { m_contentItems ??= new(); m_nonContentItems ??= new(); m_headersDict ??= new(); }
        public void SetupFromButton(iCellMenuPopupController controller, CM_PageBase page) { m_pageBase = page; page.m_clickAnywhereListeners.Add(this); }
        public void SetAnchor(GuiAnchor anchor, bool value) { }
        public void SetPosition(Vector2 position) => transform.localPosition = new(position.x, position.y, 0);
        // The game walks the content items in SetSize (UpdateContentItemVisibility).
        public void SetSize(Vector2 size) { if (m_contentItems == null) throw new NullReferenceException("m_contentItems"); m_background.Size = size; }
        public void SetHeader(string title) { }
        public void UpdatePopupChildItems() => PopupChildItems = GetComponentsInChildren<CM_Item>(true);
        public void AddNonContentItem(CM_Item item) => m_nonContentItems!.Add(item);
        public void RespawnInfoBoxFromPrefab(GameObject prefab) { }
        public void ResetHeaders() { }
        public void AddHeader(string text, int index, Action<int> callback) { }
        public void SelectHeader(int tab) { }
        public void SetContentItems(Il2CppSystem.Collections.Generic.List<iScrollWindowContent> items, float padding) => m_contentItems = items;
        public void ClearContent() => m_contentItems!.Clear();
    }
    public class CM_ScrollWindowHeader : Component { }
    /// <summary>The game's list clipping takes the renderers under its object when it is set up; <see cref="Seen"/> holds the last set.</summary>
    public class UIClipFeeder : Component
    {
        public int Setups;
        public Renderer[] Seen = Array.Empty<Renderer>();
        public void Setup(bool value)
        {
            Setups++;
            Seen = GetComponentsInChildren<Renderer>(true);
        }
    }
    public class CM_ScrollWindowInfoBox : Component
    {
        public SpriteRenderer? m_infoMainIcon;
        public BoxCollider2D? m_collider;
        public TMPro.TextMeshPro? m_infoMainTitleText, m_infoDescriptionText;
        public void SetInfoBox(string title, string subtitle, string body, string a, string b, Sprite? sprite) { if (m_infoMainIcon != null) m_infoMainIcon.sprite = sprite; }
    }
}
namespace ForgeUI
{
    internal static class NativeUi
    {
        internal static void StripLocalization(UnityEngine.GameObject go) { }
        internal static void SetText(TMPro.TextMeshPro text, string value) => text.text = value;
        internal static void SetActive(UnityEngine.Component component, bool value) => component.gameObject.SetActive(value);
        internal static string F(float n) => n.ToString();
        internal static string Vec(UnityEngine.Vector2 v) => "";
        internal static string Vec(UnityEngine.Vector3 v) => "";
        internal static string Describe(CellMenu.CM_ScrollWindow window) => "";
        internal static string DrawnBy(UnityEngine.Transform t) => "";
        internal static string Structure(UnityEngine.GameObject go) => "";
        internal static UnityEngine.Camera? CameraFor(UnityEngine.Transform t) => null;
    }
    public enum ArrowDirection { Up, Down, Left, Right }
    public static class UIKitSprites
    {
        public static UnityEngine.Sprite Pixel = new(), Outline = new(), Chamfer = new(), ChamferLine = new(), GlowBlob = new(), Infinity = new(), CornerBand = new();
        public static UnityEngine.Sprite Arrow(ArrowDirection direction) => Pixel;
        public static UnityEngine.Sprite ArrowGlyph(ArrowDirection direction) => Pixel;
    }
    /// <summary>Offline the menu copy of a picture is the picture itself.</summary>
    internal static class IconSilhouette { internal static UnityEngine.Sprite? Trimmed(UnityEngine.Sprite source) => source; }
    internal static class HudDraw { internal static void Forget() { } }
    public enum HudAlign : byte { Left, Centre, Right }
    /// <summary>The screen as the test sets it (the offline scene has no game camera to measure it from).</summary>
    internal static class HudScreen
    {
        internal static bool Measurable;
        internal static float CentreX, CentreY, HalfWidth, HalfHeight;
        internal static bool Measure(PUI_Inventory list) => Measurable;
        internal static int Version => 0;
        internal static void Reset() { }
        /// <summary>The test's HUD camera sees the list's plane straight on: world bounds go into the list's space unchanged.</summary>
        internal static bool Project(UnityEngine.Bounds bounds, ref float left, ref float bottom, ref float right, ref float top)
        {
            Hud.Extend(bounds, Hud.ListSpace, ref left, ref bottom, ref right, ref top);
            return right > left;
        }
        internal static bool WorldToHud(HudElement element, UnityEngine.Vector3 world, out UnityEngine.Vector2 point, out bool behind) { point = default; behind = false; return false; }
    }
}

// The game's HUD: the player layer and its weapon list.
public class GuiManager { public static PlayerGuiLayer? PlayerLayer; public static CellMenu.MainMenuGuiLayer? MainMenuLayer; }
public class PlayerGuiLayer { public PUI_Inventory? Inventory; public PUI_LocalPlayerStatus? m_playerStatus; }
public class PUI_LocalPlayerStatus : UnityEngine.Component { public UnityEngine.SpriteRenderer? m_health1, m_health2, m_shield1, m_shield2; public TMPro.TextMeshPro? m_pulseText, m_healthText, m_infectionText, m_shieldText; }
public class PUI_Inventory : UnityEngine.Component { public readonly Dictionary<int, PUI_InventoryItem> m_inventorySlots = new(); }
public class PUI_InventoryItem : UnityEngine.Component { public TMPro.TextMeshPro? m_slim_archetypeName; }
