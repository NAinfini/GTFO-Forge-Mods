using UnityEngine;

namespace ForgeDevelopment.Native;

/// <summary>The game's own screenshot of its rendered frame, so it works with the window behind others or minimised. Called by name by
/// <c>drive_cli.py screenshot</c> (<c>call static:ForgeDevelopment.Native.Screenshot Capture &lt;file&gt;</c>); the file lands at the end of the frame.</summary>
internal static class Screenshot
{
    internal static string Capture(string file)
    {
        ScreenCapture.CaptureScreenshot(file);
        return "capture asked: " + file;
    }
}
