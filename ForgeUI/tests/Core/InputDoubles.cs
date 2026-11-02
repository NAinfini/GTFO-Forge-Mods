// Managed stand-ins only. They do not execute Harmony patches or emulate native ray casting.
namespace Il2CppSystem
{
    public class Object
    {
        private static long _next = 1000;
        public System.IntPtr Pointer { get; set; } = (System.IntPtr)System.Threading.Interlocked.Increment(ref _next);
        public T? TryCast<T>() where T : class => this as T;
    }
}
namespace CellMenu
{
    public class CM_Item : UnityEngine.Component
    {
        public static int Probes;
        public override bool WasCollected { get { Probes++; return base.WasCollected; } }
        public UnityEngine.BoxCollider2D m_collider = null!;
        public bool m_isHovering;
        public float m_textAlphaOut = 0.5f;
        public CellMenu.TooltipInfo? m_tooltipInfo = new() { UseTooptip = true, TooltipText = "copied" };
        public string Text = "";
        public virtual void SetText(string text) => Text = text;
        public void CheckAndSetTextAlpha() { }
        public UnityEngine.Events.UnityEvent m_onBtnPress = new();
        public void Setup() { }
        public void ForcePopupLayer(bool value, object? unused = null) { }
        public void SetVisible(bool visible) => gameObject.SetActive(visible);
        public void OnBtnPress() { }
        public void OnHoverIn() { }
        public void OnHoverOut() { }
    }
    public class CM_LobbyScrollItem : CM_Item
    {
        public CM_PlayerLobbyBar m_parentBar = null!;
        public bool m_useInfoPopup;
        public UnityEngine.Component? m_infoPopup;
        public TMPro.TextMeshPro? m_nameText, m_subTitleText, m_descText;
        public UnityEngine.SpriteRenderer? m_icon, m_emptyIcon;
    }
    public class CM_InventorySlotItem : CM_LobbyScrollItem { public void SetupAsWeaponPicker() { } }
}
namespace HarmonyLib
{
    [System.AttributeUsage(System.AttributeTargets.Class)]
    public sealed class HarmonyPatch : System.Attribute
    {
        public HarmonyPatch() { }
        public HarmonyPatch(System.Type type, string method) { }
    }
    public sealed class HarmonyPrefix : System.Attribute { }
    public sealed class HarmonyPostfix : System.Attribute { }
    public static class AccessTools
    {
        public static System.Reflection.MethodInfo Method(System.Type type, string name) => type.GetMethod(name)!;
        public static System.Reflection.MethodInfo[] GetDeclaredMethods(System.Type type) => type.GetMethods();
    }
}
namespace ForgeUI
{
    /// <summary>The game-HUD hide state the HUD layout reads; nothing is hidden in the tests.</summary>
    internal static class GameHud
    {
        internal static GameHudPart Hidden => GameHudPart.None;

        internal static void Own(UnityEngine.GameObject root) { }
    }

    /// <summary>Whether a game screen (map, terminal, menu) is open; set by the tests.</summary>
    internal static class HudFocus
    {
        internal static bool Away;
    }

    /// <summary>The game's gear screens; the language switch only asks them to redraw.</summary>
    internal static class GearNames
    {
        internal static void LanguageChanged() { }
    }

    internal static class Plugin
    {
        internal const string Id = "test";
        internal static readonly System.Collections.Generic.List<string> Errors = new();
        internal static void Error(string message) => Errors.Add(message);
        internal static float OffsetX = 0, OffsetY = 0, HudOffsetX = 0, HudOffsetY = 0, HudSafeMargin = 0;
    }
}
