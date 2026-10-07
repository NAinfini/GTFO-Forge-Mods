using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using ForgeWeaponEnergyLabExperimental.Native;
using Gear;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace ForgeWeaponEnergyLabExperimental.Presentation;

/// <summary>Small baked or provider PNG previews. Lobby jobs do not instantiate full weapon prefabs.</summary>
internal static class EnergyIcons
{
    private sealed class Job
    {
        internal GearIDRange Gear = null!;
        internal Il2CppSystem.Action<IconRenderJobResult> Callback = null!;
        internal string Icon = "";
        internal int Width, Height, Frame;
    }
    private readonly record struct Icon(Texture2D Texture, Sprite Sprite);
    // Keyed by source: a provider's PNG path or a built-in model key. Each gun shows its own model's icon.
    private static readonly Dictionary<string, Icon> Icons = new(StringComparer.Ordinal);
    private static readonly Dictionary<uint, string> Checksums = new();
    private static readonly Dictionary<(string, int, int), RenderTexture> Targets = new();
    private static readonly Queue<Job> Jobs = new();

    private static string Source(EnergyGear gear) => gear.Definition.Model.IconPath ?? gear.Definition.Model.Key;

    internal static IEnumerator Preload()
    {
        while (!EnergyGearRegistry.Ready) yield return null;
        foreach (var gear in EnergyGears.All)
        {
            var source = Source(gear);
            if (Icons.ContainsKey(source)) continue;
            byte[] png;
            if (gear.Definition.Model.IconPath is { } path) png = File.ReadAllBytes(path);
            else
            {
                var name = "ForgeWeaponEnergyLabExperimental.Icons." + source + ".png";
                using var stream = typeof(EnergyIcons).Assembly.GetManifestResourceStream(name)
                    ?? throw new InvalidDataException("Baked icon is missing: " + name);
                using var bytes = new MemoryStream(); stream.CopyTo(bytes);
                png = bytes.ToArray();
            }
            if (png.Length < 32 || png.Length > 1024 * 1024) throw new InvalidDataException("Icon size budget exceeded: " + source);
            var native = new Il2CppStructArray<byte>(png);
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "Energy icon " + gear.Definition.Id };
            if (!ImageConversion.LoadImage(texture, native, true)) throw new InvalidDataException("Cannot decode icon: " + source);
            GC.KeepAlive(native);
            texture.wrapMode = TextureWrapMode.Clamp; texture.filterMode = FilterMode.Bilinear;
            Icons.Add(source, new Icon(AssetRetention.Keep(texture),
                AssetRetention.Keep(Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(.5f, .5f), 100f))));
            yield return null;
        }
    }

    internal static void Register(GearIDRange gear, EnergyGear energy) => Checksums[gear.GetChecksum()] = Source(energy);
    internal static bool OwnsChecksum(uint checksum) => Checksums.ContainsKey(checksum);
    internal static bool TryGetSprite(uint checksum, out Sprite sprite)
    {
        sprite = null!;
        if (!Checksums.TryGetValue(checksum, out var source) || !Icons.TryGetValue(source, out var icon) || icon.Sprite == null) return false;
        sprite = icon.Sprite; return true;
    }

    internal static bool Request(GearIDRange gear, Il2CppSystem.Action<IconRenderJobResult> callback, IconRenderSettings settings)
    {
        if (gear == null || EnergyGears.ForCategory(gear.GetCompID(eGearComponent.Category)) is not { } energy) return false;
        if (callback == null)
        {
            Plugin.Error("Energy icon job has no callback: " + new ArgumentNullException(nameof(callback)));
            return true;
        }
        if (PresentationPreload.Failed || settings == null || settings.resX < 32 || settings.resX > 2048 ||
            settings.resY < 32 || settings.resY > 2048 || Jobs.Count >= 64)
        {
            var reason = PresentationPreload.Failed ? "preload-failed" : Jobs.Count >= 64 ? "queue-full" : "invalid-size";
            Plugin.Error("Energy icon request refused: " + new InvalidOperationException(reason));
            Complete(gear, callback, null);
            return true;
        }
        Jobs.Enqueue(new Job { Gear = gear, Callback = callback, Icon = Source(energy), Width = settings.resX, Height = settings.resY, Frame = Time.frameCount });
        return true;
    }

    internal static void Tick()
    {
        for (var count = 0; count < 4 && Jobs.Count != 0; count++)
        {
            var job = Jobs.Peek();
            if (PresentationPreload.Failed)
            {
                Jobs.Dequeue();
                Complete(job.Gear, job.Callback, null);
                continue;
            }
            if (job.Frame >= Time.frameCount || !PresentationPreload.Ready && !Icons.ContainsKey(job.Icon)) return;
            Jobs.Dequeue();
            RenderTexture? target = null;
            try
            {
                if (!Icons.TryGetValue(job.Icon, out var icon)) throw new InvalidOperationException("Energy icon is unavailable: " + job.Icon);
                target = Target(job.Icon, icon.Texture, job.Width, job.Height);
            }
            catch (Exception error) { Plugin.Error("Energy icon rendering failed: " + error); }
            Complete(job.Gear, job.Callback, target);
        }
    }

    // A refused or failed energy job completes with no texture; it never renders the donor gun.
    private static void Complete(GearIDRange gear, Il2CppSystem.Action<IconRenderJobResult> callback, RenderTexture? target)
    {
        try { callback.Invoke(new IconRenderJobResult { gearID = gear, icon = target }); }
        catch (Exception error) { Plugin.Error("Energy icon callback failed: " + error); }
    }

    private static RenderTexture Target(string icon, Texture2D texture, int width, int height)
    {
        var key = (icon, width, height);
        if (Targets.TryGetValue(key, out var cached) && cached != null && cached.IsCreated()) return cached;
        if (Targets.Count >= 64 && !Targets.ContainsKey(key)) throw new InvalidOperationException("Energy preview target budget exceeded.");
        var target = cached != null ? cached : AssetRetention.Keep(new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32) { name = "Energy preview " + texture.name });
        var ownsTarget = cached == null;
        var previous = RenderTexture.active;
        try
        {
            try
            {
                if (!target.Create()) throw new InvalidOperationException("Cannot create energy preview target.");
                var sourceAspect = (float)texture.width / texture.height;
                var targetAspect = (float)width / height;
                // Cover, as vanilla frames do: the icon keeps the gun inside the widest band, so only empty space is cropped.
                var scale = targetAspect > sourceAspect ? new Vector2(1f, sourceAspect / targetAspect) : new Vector2(targetAspect / sourceAspect, 1f);
                Graphics.Blit(texture, target, scale, (Vector2.one - scale) * .5f);
                Targets[key] = target;
                return target;
            }
            finally
            {
                // Restore before cleanup and before returning to the native UI callback.
                RenderTexture.active = previous;
            }
        }
        catch
        {
            // Failed recreations keep their cached identity, but not partial GPU contents.
            // Preserve the rendering error even if native cleanup itself fails.
            try { target.Release(); }
            catch (Exception cleanup) { Plugin.Error("Energy preview target release failed: " + cleanup); }
            if (ownsTarget)
            {
                try { UnityEngine.Object.Destroy(target); }
                catch (Exception cleanup) { Plugin.Error("Energy preview target destruction failed: " + cleanup); }
            }
            throw;
        }
    }
}
