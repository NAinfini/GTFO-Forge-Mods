using System;
using System.Collections.Generic;

namespace ForgeEngine.Framework;

/// <summary>The authored room identity checked against Map's generation receipt.</summary>
public readonly record struct AuthoringRoomIdentity(string ZoneAuthorId, string RoomId, string RoomRevision, string InstanceId);

/// <summary>Native coordinates of one generated zone, with an authored identity when the reference names a
/// specific room instance. Native unique-prefab references leave Authored empty.</summary>
public readonly record struct AuthoringRoomScope(int Dimension, int Layer, int LocalZoneIndex,
    AuthoringRoomIdentity? Authored = null);

/// <summary>The stable identities Development needs from a generated-room match. Pose and native objects remain
/// domain-owned; diagnostics only need to say which geomorph and zone were actually matched.</summary>
public readonly record struct AuthoringRoomHit(int GeomorphInstanceId, int ZoneInstanceId);

/// <summary>Result of asking the one installed map provider to resolve an authored room reference in the current
/// generated world. Refusals are explicit; no provider means diagnostics cannot verify creation context.</summary>
public sealed class AuthoringRoomResolution
{
    public const string ResolverUnavailable = "resolver-unavailable";
    public const string NoWorld = "no-world";
    public const string PrefabNotLoaded = "prefab-not-loaded";
    public const string ZoneUnresolved = "zone-unresolved";
    public const string NoRoom = "no-room";
    public const string MultipleRooms = "multiple-rooms";

    public AuthoringRoomResolution(string? refusal, IReadOnlyList<AuthoringRoomHit> rooms)
    {
        Refusal = refusal;
        Rooms = rooms ?? throw new ArgumentNullException(nameof(rooms));
    }

    public string? Refusal { get; }
    public IReadOnlyList<AuthoringRoomHit> Rooms { get; }
    public AuthoringRoomHit? Room => Refusal == null && Rooms.Count == 1 ? Rooms[0] : null;

    internal static AuthoringRoomResolution Unavailable()
        => new(ResolverUnavailable, Array.Empty<AuthoringRoomHit>());
}

public delegate AuthoringRoomResolution AuthoringRoomResolver(long worldEpoch, string sourcePrefab, AuthoringRoomScope scope);

public sealed partial class RuntimeModuleHandle
{
    /// <summary>Registers this provider as the one authoring room resolver for the process. This is a diagnostic
    /// observation service only; it creates no capability and is removed with the provider registration.</summary>
    public void RegisterAuthoringRoomResolver(AuthoringRoomResolver resolver)
        => kernel.RegisterAuthoringRoomResolver(this, resolver);
}

public sealed partial class RuntimeKernel
{
    private string? authoringRoomProvider;
    private long authoringRoomGeneration;
    private AuthoringRoomResolver? authoringRoomResolver;

    internal void RegisterAuthoringRoomResolver(RuntimeModuleHandle owner, AuthoringRoomResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(resolver);
        Mutable();
        RuntimeJson.Require(owner.IsRegistered, "module-unregistered", owner.ProviderId);
        RuntimeJson.Require(authoringRoomResolver == null
            || (authoringRoomProvider == owner.ProviderId && authoringRoomGeneration == owner.Generation),
            "authoring-room-resolver-conflict", owner.ProviderId);
        authoringRoomProvider = owner.ProviderId;
        authoringRoomGeneration = owner.Generation;
        authoringRoomResolver = resolver;
    }

    /// <summary>Asks the currently registered domain owner to resolve an authored room. Missing support is an
    /// explicit diagnostic refusal, never a guessed room and never a dependency on a domain assembly.</summary>
    public AuthoringRoomResolution ResolveAuthoringRoom(long worldEpoch, string sourcePrefab, AuthoringRoomScope scope)
    {
        ReadThread();
        var resolver = authoringRoomResolver;
        if (resolver == null) return AuthoringRoomResolution.Unavailable();
        try { return resolver(worldEpoch, sourcePrefab ?? "", scope) ?? AuthoringRoomResolution.Unavailable(); }
        catch (RuntimeContractException) { throw; }
        catch (Exception error)
        {
            throw new RuntimeContractException("authoring-room-resolver-failed",
                error.GetType().Name + ": " + error.Message);
        }
    }

    private void RemoveAuthoringRoomResolver(string provider, long generation)
    {
        if (authoringRoomResolver == null || authoringRoomProvider != provider || authoringRoomGeneration != generation) return;
        authoringRoomResolver = null;
        authoringRoomProvider = null;
        authoringRoomGeneration = 0;
    }
}
