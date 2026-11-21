using System.Text.Json;

namespace ForgeDevelopment.Native;

/// <summary>The JSON options of Development's own report files (they are written with <c>ForgeCore.Store.WriteBytes</c>).</summary>
internal static class CaptureFiles
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
}
