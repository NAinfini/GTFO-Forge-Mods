using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;

namespace ForgePerformance.Crowd;

/// <summary>The probe's native helper (tools/NativeProbe, built by hand and put beside this assembly as
/// <c>ForgePerformance.NativeProbe.dll</c> for a measurement run; it is not packaged). With the file present and <c>Probe</c>
/// on it samples the stacks of the main and the render thread, counts and times the Direct3D 11 calls, and on request writes a
/// GPU timeline of a few frames. Without the file the probe lines simply lack these columns. Everything is written to the
/// session's folder under <c>BepInEx/PerformanceLogs</c>.</summary>
internal static unsafe class ProbeNative
{
    internal const string FileName = "ForgePerformance.NativeProbe.dll";
    internal static bool Loaded { get; private set; }
    private static string _folder = "";
    private static delegate* unmanaged[Cdecl]<char*, uint*, int, int, int> _start;
    private static delegate* unmanaged[Cdecl]<void> _stop;
    private static delegate* unmanaged[Cdecl]<void*, int> _installD3D;
    private static delegate* unmanaged[Cdecl]<int, int> _capture;
    private static delegate* unmanaged[Cdecl]<int> _captureState;
    private static delegate* unmanaged[Cdecl]<ulong*, double*, ulong*, ulong*, uint*, int> _counters;
    private static delegate* unmanaged[Cdecl]<int, IntPtr> _counterName;
    private static delegate* unmanaged[Cdecl]<ulong> _samples;
    private static IntPtr _event, _eventData;
    private static uint _mainThread;
    private static bool _sampling, _d3d;
    private static string[] _names = Array.Empty<string>();
    private static ulong[] _calls = Array.Empty<ulong>(), _lastCalls = Array.Empty<ulong>();
    private static double[] _ms = Array.Empty<double>(), _lastMs = Array.Empty<double>();
    private static ulong _instances, _indices, _lastInstances, _lastIndices;
    private static readonly List<(Camera Camera, CameraEvent Event, CommandBuffer Buffer)> Labels = new();
    private static JitMap? _jit;

    internal static void Start(string pluginFolder, string sessionFolder, Action<string> line)
    {
        var path = Path.Combine(pluginFolder, FileName);
        if (!File.Exists(path)) { line("type=perf_probe_native\tstate=absent\tpath=" + path); return; }
        var library = NativeLibrary.Load(path);
        _start = (delegate* unmanaged[Cdecl]<char*, uint*, int, int, int>)NativeLibrary.GetExport(library, "fpp_start");
        _stop = (delegate* unmanaged[Cdecl]<void>)NativeLibrary.GetExport(library, "fpp_stop");
        _installD3D = (delegate* unmanaged[Cdecl]<void*, int>)NativeLibrary.GetExport(library, "fpp_install_d3d");
        _capture = (delegate* unmanaged[Cdecl]<int, int>)NativeLibrary.GetExport(library, "fpp_capture");
        _captureState = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(library, "fpp_capture_state");
        _counters = (delegate* unmanaged[Cdecl]<ulong*, double*, ulong*, ulong*, uint*, int>)NativeLibrary.GetExport(library, "fpp_counters");
        _counterName = (delegate* unmanaged[Cdecl]<int, IntPtr>)NativeLibrary.GetExport(library, "fpp_counter_name");
        _samples = (delegate* unmanaged[Cdecl]<ulong>)NativeLibrary.GetExport(library, "fpp_samples");
        _event = ((delegate* unmanaged[Cdecl]<IntPtr>)NativeLibrary.GetExport(library, "fpp_render_event"))();
        _eventData = ((delegate* unmanaged[Cdecl]<IntPtr>)NativeLibrary.GetExport(library, "fpp_render_event_data"))();
        _folder = sessionFolder;
        Directory.CreateDirectory(_folder);
        _mainThread = GetCurrentThreadId();
        var names = new List<string>();
        for (var i = 0; ; i++)
        {
            var name = Marshal.PtrToStringAnsi(_counterName(i));
            if (string.IsNullOrEmpty(name)) break;
            names.Add(name);
        }
        _names = names.ToArray();
        _calls = new ulong[_names.Length]; _lastCalls = new ulong[_names.Length];
        _ms = new double[_names.Length]; _lastMs = new double[_names.Length];
        _jit = new JitMap(Path.Combine(_folder, "jit.tsv"));
        Loaded = true;
        line("type=perf_probe_native\tstate=loaded\tfolder=" + _folder + "\tqpc_hz=" + Stopwatch.Frequency);
    }

    /// <summary>Once per frame on the main thread: the frame mark for the render thread, and the late starts (the sampler needs
    /// the render thread, the Direct3D hooks a texture).</summary>
    internal static void Frame(uint renderThread, Action<string> line)
    {
        if (!Loaded) return;
        if (!_sampling && renderThread != 0)
        {
            var threads = stackalloc uint[2] { _mainThread, renderThread };
            int opened;
            fixed (char* folder = _folder) opened = _start(folder, threads, 2, 1000);
            _sampling = true;
            line("type=perf_probe_native\tstate=sampling\tthreads=" + opened + "\tmain=" + _mainThread + "\trender=" + renderThread);
        }
        if (!_d3d) InstallD3D(line);
        if (_d3d) GL.GLIssuePluginEvent(_event, 1);
        if (Labels.Count != 0 && _captureState() == 3) RemoveLabels();
    }

    /// <summary>The Direct3D hooks need a texture's native pointer: tried each frame until they are installed, which is the session's first frames.</summary>
    private static void InstallD3D(Action<string> line)
    {
        var texture = Texture2D.whiteTexture;
        var native = texture == null ? IntPtr.Zero : texture.GetNativeTexturePtr();
        if (native == IntPtr.Zero) return;
        _d3d = _installD3D((void*)native) != 0;
        line("type=perf_probe_native\tstate=d3d\tinstalled=" + (_d3d ? 1 : 0));
    }

    /// <summary>Per-frame Direct3D figures of the window that just closed, appended to its line.</summary>
    internal static void Append(StringBuilder line, int frames)
    {
        if (!Loaded || !_d3d) return;
        uint shaders;
        ulong instances, indices;
        fixed (ulong* calls = _calls) fixed (double* ms = _ms) _counters(calls, ms, &instances, &indices, &shaders);
        _instances = instances; _indices = indices;
        var n = Math.Max(1, frames);
        double draws = 0, drawMs = 0, allMs = 0;
        for (var i = 0; i < _names.Length; i++)
        {
            var calls = _calls[i] - _lastCalls[i];
            var ms = _ms[i] - _lastMs[i];
            allMs += ms;
            if (i < 6) { draws += calls; drawMs += ms; }
            if (calls != 0)
                line.Append("\td3d_").Append(_names[i]).Append("_n=").Append(F(calls / (double)n))
                    .Append("\td3d_").Append(_names[i]).Append("_ms=").Append(F(ms / n));
            _lastCalls[i] = _calls[i]; _lastMs[i] = _ms[i];
        }
        line.Append("\td3d_draws=").Append(F(draws / n)).Append("\td3d_draw_ms=").Append(F(drawMs / n))
            .Append("\td3d_hooked_ms=").Append(F(allMs / n))
            .Append("\td3d_instances=").Append(F((_instances - _lastInstances) / (double)n))
            .Append("\td3d_indices=").Append(F((_indices - _lastIndices) / (double)n))
            .Append("\tshaders_created=").Append(shaders & 0xFFFFFF).Append("\td3d_table_swaps=").Append(shaders >> 24).Append("\tsamples=").Append(_samples());
        _lastInstances = _instances; _lastIndices = _indices;
    }

    private static readonly CameraEvent[] Events =
    {
        CameraEvent.BeforeDepthTexture, CameraEvent.AfterDepthTexture, CameraEvent.BeforeDepthNormalsTexture, CameraEvent.AfterDepthNormalsTexture,
        CameraEvent.BeforeGBuffer, CameraEvent.AfterGBuffer, CameraEvent.BeforeLighting, CameraEvent.AfterLighting,
        CameraEvent.BeforeFinalPass, CameraEvent.AfterFinalPass, CameraEvent.BeforeForwardOpaque, CameraEvent.AfterForwardOpaque,
        CameraEvent.BeforeImageEffectsOpaque, CameraEvent.AfterImageEffectsOpaque, CameraEvent.BeforeSkybox, CameraEvent.AfterSkybox,
        CameraEvent.BeforeForwardAlpha, CameraEvent.AfterForwardAlpha, CameraEvent.BeforeImageEffects, CameraEvent.AfterImageEffects,
        CameraEvent.AfterEverything, CameraEvent.BeforeReflections, CameraEvent.AfterReflections,
        CameraEvent.BeforeHaloAndLensFlares, CameraEvent.AfterHaloAndLensFlares
    };

    /// <summary>Asks for a GPU timeline of the next <paramref name="frames"/> frames (gpu-timeline-N.csv). Every enabled camera
    /// gets a label at each camera event for the capture: label = 100 × (camera index + 1) + event; the answer lists the cameras.</summary>
    internal static string Capture(int frames)
    {
        if (!Loaded || !_d3d) return "native=absent";
        if (Labels.Count != 0) return "busy";
        var cameras = Camera.allCameras;
        var answer = new StringBuilder();
        for (var c = 0; c < cameras.Length; c++)
        {
            var camera = cameras[c];
            if (camera == null) continue;
            answer.Append(c + 1).Append('=').Append(camera.name).Append("/depth ").Append(F(camera.depth)).Append('/')
                .Append(camera.actualRenderingPath).Append('/').Append(camera.pixelWidth).Append('x').Append(camera.pixelHeight).Append("; ");
            foreach (var cameraEvent in Events)
            {
                var buffer = new CommandBuffer { name = "ForgePerformance probe label" };
                buffer.IssuePluginEventAndData(_eventData, 100 * (c + 1) + (int)cameraEvent, IntPtr.Zero);
                camera.AddCommandBuffer(cameraEvent, buffer);
                Labels.Add((camera, cameraEvent, buffer));
            }
        }
        var armed = _capture(Math.Clamp(frames, 1, 64));
        if (armed == 0) RemoveLabels();
        return "armed=" + armed + "; cameras: " + answer;
    }

    internal static string CaptureState() => !Loaded ? "native=absent" : _captureState() switch { 0 => "idle", 1 => "capturing", 2 => "resolving", _ => "done" };

    private static void RemoveLabels()
    {
        foreach (var (camera, cameraEvent, buffer) in Labels)
        {
            if (camera != null) camera.RemoveCommandBuffer(cameraEvent, buffer);
            buffer.Dispose();
        }
        Labels.Clear();
    }

    /// <summary>Stops the sampler and writes what names its addresses: every game method's entry (il2cpp-methods.tsv; walking
    /// every class costs seconds and memory, so it is done once, at the end of a measurement) and the shader list.</summary>
    internal static string Finish()
    {
        if (!Loaded) return "native=absent";
        _stop();
        _sampling = true; // never started again in this process
        _jit?.Flush();
        var written = 0;
        using (var writer = new StreamWriter(Path.Combine(_folder, "il2cpp-methods.tsv"), false, new UTF8Encoding(false)))
        {
            uint count = 0;
            var assemblies = IL2CPP.il2cpp_domain_get_assemblies(IL2CPP.il2cpp_domain_get(), ref count);
            for (var a = 0u; a < count; a++)
            {
                var image = IL2CPP.il2cpp_assembly_get_image(assemblies[a]);
                var imageName = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_image_get_name(image));
                var classes = IL2CPP.il2cpp_image_get_class_count(image);
                for (var c = 0u; c < classes; c++)
                {
                    var klass = IL2CPP.il2cpp_image_get_class(image, c);
                    if (klass == IntPtr.Zero) continue;
                    var type = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_namespace(klass)) + "." + Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass));
                    var iterator = IntPtr.Zero;
                    for (IntPtr method; (method = IL2CPP.il2cpp_class_get_methods(klass, ref iterator)) != IntPtr.Zero;)
                    {
                        var entry = *(IntPtr*)method;
                        if (entry == IntPtr.Zero) continue;
                        writer.Write(((ulong)entry).ToString("x", CultureInfo.InvariantCulture)); writer.Write('\t');
                        writer.Write(imageName); writer.Write('\t'); writer.Write(type); writer.Write('.');
                        writer.WriteLine(Marshal.PtrToStringAnsi(IL2CPP.il2cpp_method_get_name(method)));
                        written++;
                    }
                }
            }
        }
        return "samples=" + _samples() + ";methods=" + written + ";folder=" + _folder;
    }

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    /// <summary>Where the runtime put each method it compiled after the probe started (address, size, name): the sampler's
    /// addresses inside the mods' own code are named from this.</summary>
    private sealed class JitMap : EventListener
    {
        private static StreamWriter? _writer;
        private static readonly object Gate = new();
        internal JitMap(string path) { lock (Gate) _writer = new StreamWriter(path, false, new UTF8Encoding(false)); EnableKnown(); }
        private static EventSource? _runtime;
        private void EnableKnown() { if (_runtime != null) EnableEvents(_runtime, EventLevel.Verbose, (EventKeywords)0x10); }
        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name != "Microsoft-Windows-DotNETRuntime") return;
            _runtime = source;
            if (_writer != null) EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x10);
        }
        protected override void OnEventWritten(EventWrittenEventArgs data)
        {
            if (data.EventName == null || !data.EventName.StartsWith("MethodLoadVerbose", StringComparison.Ordinal) || data.Payload == null || data.PayloadNames == null) return;
            object? start = null, size = null, space = null, name = null;
            for (var i = 0; i < data.PayloadNames.Count; i++)
                switch (data.PayloadNames[i])
                {
                    case "MethodStartAddress": start = data.Payload[i]; break;
                    case "MethodSize": size = data.Payload[i]; break;
                    case "MethodNamespace": space = data.Payload[i]; break;
                    case "MethodName": name = data.Payload[i]; break;
                }
            lock (Gate) _writer?.WriteLine(Convert.ToUInt64(start, CultureInfo.InvariantCulture).ToString("x", CultureInfo.InvariantCulture) + "\t" + size + "\t" + space + "." + name);
        }
        internal void Flush() { lock (Gate) _writer?.Flush(); }
    }
}
