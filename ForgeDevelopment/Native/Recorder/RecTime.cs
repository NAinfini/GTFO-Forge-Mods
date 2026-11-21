using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using UnityEngine;

namespace ForgeDevelopment.Native;

/// <summary>
/// The game's own answers for everything a record carries: process milliseconds, Unity frame, the game's network
/// time, the multiplayer role, the player slot, the level, this package's world epoch, the camera aim a
/// bookmark records and the screenshot itself. All of it is read on the game's main thread at enqueue time, because
/// most of it is IL2CPP state that must not be touched from the writer thread.
///
/// </summary>
internal static class RecTime
{
    private static readonly long Origin = Environment.TickCount64;
    private static bool _slotFailed, _snetFailed, _aimFailed;

    internal static long Milliseconds => Environment.TickCount64 - Origin;

    internal static int Frame { get { try { return Time.frameCount; } catch (Exception) { return -1; } } } // Il2Cpp read guard: -1 is the documented "no frame" stamp.
    internal static string GameVersion { get { try { return Application.version; } catch (Exception) { return "unavailable"; } } } // Il2Cpp read guard: "unavailable" is the explicit recorded value.
    internal static string UnityVersion { get { try { return Application.unityVersion; } catch (Exception) { return "unavailable"; } } } // Il2Cpp read guard: "unavailable" is the explicit recorded value.

    /// <summary>The role the network session reports, or "unavailable" before the game has a local network player.
    /// A process with no session is not a client; <see cref="ForgeCore.Session.IsHost"/> alone cannot tell the two
    /// apart, which is what made an early recorder header claim a role the session never had.</summary>
    internal static string Role
    {
        get
        {
            try { return ForgeCore.Session.LocalLookup == 0 ? "unavailable" : ForgeCore.Session.IsHost ? "host" : "client"; }
            catch (Exception) { return "unavailable"; } // Il2Cpp read guard: "unavailable" is the explicit recorded value.
        }
    }

    /// <summary>The game's own session slot, or "unavailable" once the read has failed. The slot manager is missing
    /// outside a session, so the failure is remembered instead of paying the exception every record.</summary>
    internal static string Slot
    {
        get
        {
            if (_slotFailed) return "unavailable";
            try
            {
                var player = SNetwork.SNet.LocalPlayer;
                if (player == null) return "unavailable";
                return player.PlayerSlotIndex().ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception) { _slotFailed = true; return "unavailable"; } // Il2Cpp read guard: the failure flag stops retries and "unavailable" is the explicit recorded value.
        }
    }

    /// <summary>The player's own synchronized session time. SNet leaves it at zero until the player is synchronized
    /// with the game, which is exactly when a record becomes alignable across machines; a zero is reported as no time
    /// rather than as a real one.</summary>
    internal static float? SNetTime
    {
        get
        {
            if (_snetFailed) return null;
            try
            {
                var player = SNetwork.SNet.LocalPlayer;
                if (player == null) return null;
                var time = player.m_syncTime;
                return time > 0f && float.IsFinite(time) ? time : null;
            }
            catch (Exception) { _snetFailed = true; return null; } // Il2Cpp read guard: the failure flag stops retries and null is the explicit "no sync time" value.
        }
    }

    /// <summary>The expedition the process is in: the rundown's own active-expedition key, which is the identity the
    /// rest of this package already reports. It is "unavailable" outside a rundown rather than a made-up id.</summary>
    internal static string Level
    {
        get
        {
            var key = ForgeCore.Level.ActiveKey;
            return string.IsNullOrEmpty(key) ? "unavailable" : key;
        }
    }

    private static long _worldEpoch;
    /// <summary>One world is one built level: the count goes up when a level build starts and again when it is cleaned up, so
    /// a record, lease or readback taken in one world never matches the next. 0 before the first build.</summary>
    internal static long WorldEpoch => System.Threading.Interlocked.Read(ref _worldEpoch);
    internal static void AdvanceWorld() => System.Threading.Interlocked.Increment(ref _worldEpoch);

    /// <summary>Writes the producer part of every record. The writer is already inside the record object; the
    /// recorder's disk writer prefixes the schema version and sequence number.</summary>
    internal static void WriteContext(Utf8JsonWriter json, string sessionId, string channel, string kind)
    {
        json.WriteString("session", sessionId);
        json.WriteString("channel", channel);
        json.WriteString("kind", kind);
        json.WriteNumber("t", Milliseconds);
        var frame = Frame;
        if (frame >= 0) json.WriteNumber("frame", frame);
        if (SNetTime is { } snet) json.WriteNumber("snetTime", MathF.Round(snet, 3));
        json.WriteString("role", RecSession.Role);
        json.WriteString("slot", Slot);
        json.WriteString("level", Level);
        // Managed capture identity joins trace pointers to the same generation
        // of gate/area/node topology; a reused native address is not a persistent ID.
        json.WriteString("generationSessionId", RuntimeDiagnostics.SessionId);
        json.WriteString("generationId", RuntimeDiagnostics.GenerationId);
        json.WriteNumber("worldEpoch", WorldEpoch);
    }

    /// <summary>Captures the screen into the session directory. A capture that cannot be taken answers an empty path,
    /// which the session records as an error rather than as an empty file.</summary>
    internal static string WriteScreenshot(string directory, string relative)
    {
        var image = ScreenCapture.CaptureScreenshotAsTexture();
        try
        {
            byte[] bytes = ImageConversion.EncodeToPNG(image);
            if (!RecSession.WriteDocument(relative, bytes))
                throw new IOException("recorder.screenshot-queue-refused");
        }
        finally { UnityEngine.Object.Destroy(image); }
        return relative;
    }

    /// <summary>The player's own view when a bookmark is taken, so the bookmark says what they were looking at. It is
    /// a best-effort read: anything the game refuses answers null and the bookmark stays a label.</summary>
    internal static (string Name, string Value)[]? Aim()
    {
        if (_aimFailed) return null;
        try
        {
            var camera = Camera.main;
            if (camera == null) return null;
            var position = camera.transform.position;
            var forward = camera.transform.forward;
            return new[]
            {
                ("camera", RecReflect.Truncate(camera.name, 64)),
                ("position", Number(position.x) + "," + Number(position.y) + "," + Number(position.z)),
                ("forward", Number(forward.x) + "," + Number(forward.y) + "," + Number(forward.z))
            };
        }
        catch (Exception) { _aimFailed = true; return null; } // Il2Cpp read guard: the failure flag stops retries and null is the explicit "no aim" value.
    }

    private static string Number(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
