// The game types GearIcons and IconJobLoad touch, with the fields they read and write (names as in the il2cpp dump).
#pragma warning disable CS8618
namespace Gear
{
    public class GearIDRange
    {
        public uint Category, Checksum;
        public uint GetCompID(eGearComponent component) => Category;
        public uint GetChecksum() => Checksum;
    }
}

public enum eGearComponent { Category }
public class IconRenderJobData { }
public class IconRenderJobResult { public Gear.GearIDRange gearID; }
public class GearIconRendering
{
    public static string? IconFile;
    public static bool TryGetGearIconFilePath(uint checksum, out string path) { path = IconFile!; return IconFile != null; }

    public class IconRenderJob
    {
        public Gear.GearIDRange gearToRender;
        public UnityEngine.GameObject loadedGearGO;
        public bool asyncLoadingStarted, asyncLoadingDone;
        public IconRenderJobData[] datas;
        public IconRenderJobResult[] results;
    }
}
