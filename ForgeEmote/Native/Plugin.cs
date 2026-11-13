using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using ForgeCore;
using ForgeEmote.Api;

namespace ForgeEmote;

[BepInPlugin(Id, "Forge Emote", Version)]
[BepInDependency("NAinfini.ForgeCore", ">=1.0.0")]
internal sealed class Plugin : BasePlugin
{
    internal const string Id = "NAinfini.ForgeEmote", Version = "1.0.0"; // package-version: NAinfini-ForgeEmote

    internal static ModLog Logger { get; private set; } = null!;
    internal static BepInEx.Logging.ManualLogSource Info { get; private set; } = null!;
    internal static ConfigEntry<bool> Readback = null!;
    internal static ConfigEntry<bool> ThirdPerson = null!, ThirdPersonGestures = null!, Orbit = null!, CrudeGestures = null!;
    internal static ConfigEntry<float> Distance = null!, Shoulder = null!, Height = null!, BlendSeconds = null!;

    public override void Load()
    {
        Logger = ForgeCore.Log.For(Id);
        Info = Log;
        Readback = Config.Bind("Debug", "Readback", false, "Write one line of body heights to the log at the start and end of your own actions and one second after (for finding why a body floats). Off by default.");
        ThirdPerson = Config.Bind("View", "ThirdPerson", true, "Dances switch to a view of yourself from behind; every other action stays in first person. Off: dances stay in first person too.");
        ThirdPersonGestures = Config.Bind("View", "ThirdPersonGestures", false, "Gestures also switch to the view from behind. Off: gestures stay in first person.");
        Distance = Config.Bind("View", "Distance", 2.4f, "How far behind you the view stands, in metres.");
        Shoulder = Config.Bind("View", "Shoulder", 0.35f, "How far to the side of you the view stands, in metres.");
        Height = Config.Bind("View", "Height", 0.25f, "How far above your head the view looks from, in metres.");
        BlendSeconds = Config.Bind("View", "BlendSeconds", 0.3f, "Seconds the view takes to move out and back.");
        Orbit = Config.Bind("View", "Orbit", true, "The mouse turns the view around you during an action.");
        CrudeGestures = Config.Bind("Display", "CrudeGestures", true, "Show rude gestures other players make. Off: they keep their normal animation on your screen.");
        Emotes.Load();
        EmoteWheel.Load();
        EmoteRunner.Load();
        Frame.OnUpdate(Id, EmoteRunner.Update);
        Frame.OnLateUpdate(Id, EmoteRunner.LateUpdate);
    }

    public override bool Unload() => false;
}
