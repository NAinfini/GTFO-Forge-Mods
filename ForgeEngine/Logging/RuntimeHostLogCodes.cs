using ForgeEngine.Framework;

namespace ForgeEngine.Logging;

/// <summary>
/// The host's own diagnostic codes in the Forge Engine log (contract §3.2 I-DIAG), written under the Runtime provider
/// through the host-only entry <see cref="RuntimeKernel.LogRuntime"/>. Each has one fixed level. They record what only
/// the game binding and the network layer see — the execution gate's parts, checkpoints, the session, the handshake,
/// the world epoch, requested steps and variable bodies — and never an SNet session or any other account identity: a
/// peer is named by its role, and the plan-set comparison by its code and counts.
/// </summary>
internal static partial class RuntimeHostLogCodes
{
    // The constants are generated from catalog/authoring/diagnostic-codes.json (artifacts/generated/diagnostics/RuntimeHostLogCodes.g.cs).
}
