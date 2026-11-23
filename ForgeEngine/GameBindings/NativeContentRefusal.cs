using System;
using ForgeCore;
using ForgeEngine.Framework;

namespace ForgeEngine.GameBindings;

/// <summary>The answer a domain's native content registration (Enemy, Weapon) gives for the exception that refused an entry: a table
/// rollback whose ownership audit failed stops Forge content until restart, a taken block id is a collision, a contract refusal keeps
/// its own code, and anything else is the domain's own invalid-content code.</summary>
internal static class NativeContentRefusal
{
    /// <summary>What stopped Forge content until the game restarts (contract I-RELOAD): a failed ownership audit after a table
    /// rollback, a content generation whose rollback failed, or content the game lost that could not be put back. The game itself
    /// goes on and the host tells the player in game. Null while Forge content runs.</summary>
    internal static Exception? Stopped { get; private set; }

    /// <summary>Stops Forge content until restart for <paramref name="reason"/>; the first reason is kept.</summary>
    internal static void Stop(Exception reason) => Stopped ??= reason ?? throw new ArgumentNullException(nameof(reason));

    /// <summary>Whether <paramref name="error"/> (or an exception inside it) is a failed ownership audit; the first one stops Forge
    /// content.</summary>
    internal static bool StopsOn(Exception error)
    {
        for (var inner = error; inner != null; inner = inner.InnerException)
            if (inner is NativeDataBlockAuditException audit) { Stop(audit); return true; }
        return false;
    }

    internal static ContentRegistrationResult Of(Exception error, string id, string invalidCode) => StopsOn(error)
        ? ContentRegistrationResult.Reject("content-native-state-unknown", id)
        : error switch
        {
            NativeDataBlockConflictException => ContentRegistrationResult.Reject("content-id-collision", error.Message),
            RuntimeContractException contract => ContentRegistrationResult.Reject(contract.Code, contract.Message),
            _ => ContentRegistrationResult.Reject(invalidCode, error.GetBaseException().Message)
        };
}
