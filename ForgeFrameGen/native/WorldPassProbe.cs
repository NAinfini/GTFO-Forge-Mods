using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace ForgeFrameGen;

/// <summary>The development driver's readbacks of every stage of the pass and of the world camera's command buffers.</summary>
internal static unsafe partial class WorldPass
{
    private static readonly Dictionary<string, (float Sum, int Frame)> ProbeLast = new();
    private static readonly Dictionary<(int, int, TextureFormat), Texture2D> ProbeReadback = new();

    /// <summary>Development driver: every command buffer on the world camera, per camera event, by name and size.</summary>
    internal static string CommandBuffers()
    {
        var camera = WorldCamera();
        if (camera == null) return "no world camera";
        var text = new StringBuilder("count=" + camera.commandBufferCount.ToString());
        foreach (CameraEvent at in Enum.GetValues(typeof(CameraEvent)))
        {
            var buffers = camera.GetCommandBuffers(at);
            if (buffers == null || buffers.Length == 0) continue;
            text.Append(" | ").Append(at.ToString()).Append(':');
            foreach (var buffer in buffers) text.Append(" [").Append(buffer.name).Append(' ').Append(buffer.sizeInBytes.ToString()).Append("B]");
        }
        return text.ToString();
    }

    private static CommandBuffer? _held;
    private static CameraEvent _heldAt;
    private static ForgeVisual.OutlineHandle? _outline;

    /// <summary>Development driver: takes the world camera's first command buffer named <paramref name="name"/> off the camera, to see what it
    /// draws; an empty name puts the one taken back at its event.</summary>
    internal static string HoldBuffer(string name)
    {
        var camera = WorldCamera();
        if (camera == null) return "no world camera";
        if (_held != null)
        {
            camera.AddCommandBuffer(_heldAt, _held);
            _held = null;
        }
        if (name.Length == 0) return "put back; " + CommandBuffers();
        foreach (CameraEvent at in Enum.GetValues(typeof(CameraEvent)))
        {
            var buffers = camera.GetCommandBuffers(at);
            if (buffers == null) continue;
            foreach (var buffer in buffers)
            {
                if (buffer.name != name) continue;
                camera.RemoveCommandBuffer(at, buffer);
                (_held, _heldAt) = (buffer, at);
                return "taken " + name + " from " + at.ToString();
            }
        }
        return "no buffer named " + name;
    }

    /// <summary>Development driver: outlines the nearest live enemy (X-ray, so it shows through walls) and returns where it is, to look at it;
    /// a second call removes the outline.</summary>
    internal static string OutlineNearestEnemy()
    {
        if (_outline != null)
        {
            _outline.Remove();
            _outline = null;
            return "outline removed";
        }
        var camera = WorldCamera();
        if (camera == null) return "no world camera";
        var from = camera.transform.position;
        Enemies.EnemyAgent? nearest = null;
        float best = float.MaxValue;
        foreach (var agent in ForgeCore.Enemies.Live)
        {
            if (agent == null || !agent.Alive) continue;
            var at = agent.transform.position;
            float dx = at.x - from.x, dy = at.y - from.y, dz = at.z - from.z, distance = dx * dx + dy * dy + dz * dz;
            if (distance < best) (nearest, best) = (agent, distance);
        }
        if (nearest == null) return "no live enemy";
        _outline = ForgeVisual.Outlines.Add(Plugin.Id, nearest, new ForgeVisual.OutlineColor(1f, 0.2f, 0.1f), 100, 0f, true);
        var p = nearest.transform.position;
        return "outlined " + nearest.name + " look " + p.x.ToString("0.##") + " " + (p.y + 1f).ToString("0.##") + " " + p.z.ToString("0.##")
               + " distance " + Math.Sqrt(best).ToString("0.#") + " added=" + (_outline != null) + " enabled=" + ForgeVisual.Outlines.Enabled;
    }

    /// <summary>Development driver: reads every stage of the pass back from the GPU (<c>ReadPixels</c> into a float texture,
    /// so values outside 0..1 survive) and samples a 16 × 12 grid: mean and variance per channel, whether anything is non-zero, and whether
    /// the samples changed since this stage was last probed. The stages hold the last rendered frame when this runs from the driver.</summary>
    internal static string Probe()
    {
        var text = new StringBuilder("frame=" + Time.frameCount.ToString());
        Stage(text, "world", _world, TextureFormat.RGBAFloat);
        Stage(text, "colour", _color, TextureFormat.RGBAFloat);
        Stage(text, "depth", _depth, TextureFormat.RGBAFloat);
        Stage(text, "motion", _motion, TextureFormat.RGBAFloat);
        Stage(text, "output", _output, TextureFormat.RGBAFloat);
        return text.ToString();
    }

    private static void Stage(StringBuilder text, string name, RenderTexture? texture, TextureFormat format)
    {
        text.Append(" | ").Append(name).Append(": ");
        if (texture == null || texture.WasCollected || !texture.IsCreated())
        {
            text.Append("none");
            return;
        }
        if (texture.antiAliasing > 1)
        {
            text.Append("msaa, not read");
            return;
        }
        int width = texture.width, height = texture.height;
        if (!ProbeReadback.TryGetValue((width, height, format), out var readback) || readback.WasCollected)
        {
            readback = new Texture2D(width, height, format, false, true) { name = "ForgeFrameGen probe" };
            ProbeReadback[(width, height, format)] = readback;
        }
        var previous = RenderTexture.active;
        RenderTexture.active = texture;
        readback.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
        RenderTexture.active = previous;

        const int Columns = 16, Rows = 12;
        Span<double> sum = stackalloc double[4], square = stackalloc double[4];
        double signature = 0;
        bool nonZero = false;
        for (int row = 0; row < Rows; row++)
            for (int column = 0; column < Columns; column++)
            {
                var colour = readback.GetPixel((column * 2 + 1) * width / (Columns * 2), (row * 2 + 1) * height / (Rows * 2));
                for (int channel = 0; channel < 4; channel++)
                {
                    // Fields, not the indexer: the interop copy of Color.this[int] carries a broken string concatenation on its error path.
                    double value = channel switch { 0 => colour.r, 1 => colour.g, 2 => colour.b, _ => colour.a };
                    sum[channel] += value;
                    square[channel] += value * value;
                    signature += value * (1 + channel + 4 * (row * Columns + column) % 97);
                    if (value != 0 && channel < 3) nonZero = true;
                }
            }
        const int Count = Columns * Rows;
        text.Append(width).Append('x').Append(height).Append(" nonZero=").Append(nonZero);
        bool known = ProbeLast.TryGetValue(name, out var last);
        text.Append(" changed=").Append(known ? Math.Abs(last.Sum - signature) > 1e-6 ? "yes" : "no" : "first").Append(known ? "@" + (Time.frameCount - last.Frame).ToString() + "f" : "");
        ProbeLast[name] = ((float)signature, Time.frameCount);
        for (int channel = 0; channel < 4; channel++)
        {
            double mean = sum[channel] / Count;
            text.Append(' ').Append("rgba"[channel]).Append('=').Append(mean.ToString("G4")).Append('±').Append(Math.Sqrt(Math.Max(0, square[channel] / Count - mean * mean)).ToString("G3"));
        }
    }
}
