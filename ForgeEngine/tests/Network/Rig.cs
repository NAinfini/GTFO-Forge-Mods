using System;
using System.Collections.Generic;
using ForgeCore;
using ForgeCore.Tests;
using ForgeEngine.Framework;

namespace ForgeEngine.Network.Tests;

/// <summary>One session on ForgeCore's in-memory hub. Every player is a <see cref="Machine"/>: ForgeCore's real node, the
/// engine's channel on it, the engine's network and a kernel of its own.</summary>
internal sealed class Session
{
    internal readonly Hub Hub = new();

    internal Machine Join(ulong player) => new(this, player);

    /// <summary>How many packets of one engine message reached <paramref name="player"/>.</summary>
    internal int Sent(string message, ulong player) => Hub.Count(NetworkProtocol.ChannelId + "/" + message, player);
}

/// <summary>One player's machine. It supplies <see cref="EngineNet"/> the facts the game-facing half reads from the game
/// (who the players are, where a received body enters the kernel) and nothing else.</summary>
internal sealed class Machine
{
    internal static readonly RuntimeIdentity Identity = new("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "20403457");

    internal readonly Hub.Endpoint Endpoint;
    internal readonly NetChannel Channel;
    internal readonly EngineNet Net;
    internal readonly RuntimeKernel Kernel = new(Identity);
    /// <summary>Every state body this machine took, as it arrived.</summary>
    internal readonly List<(bool Complete, long Epoch)> States = new();
    internal readonly List<long> Steps = new();
    /// <summary>The state of one native domain: what the kernel's document carries besides its own variables.</summary>
    internal int Native;
    /// <summary>What this machine's presentation step answers, in place of a plan this rig does not load.</summary>
    internal Func<CommandResult>? Presents { get; set; }

    internal Machine(Session session, ulong player)
    {
        Endpoint = session.Hub.Join(player);
        Channel = Endpoint.Node.Open(NetworkProtocol.ChannelId);
        // The rig's one native source: its whole value, in the document and in a change alike.
        Kernel.NativeStateCapture = _ => RuntimeJson.From(new { rig = new { value = Native } });
        Kernel.NativeStateValidate = static _ => { };
        Kernel.NativeStateRestore = state => Native = state.GetProperty("rig").GetProperty("value").GetInt32();
        Kernel.NativeStateWriteDelta = writer => { writer.Write7BitEncodedInt(1); writer.Write(Native); };
        Kernel.NativeStateReadDelta = (reader, count) =>
        {
            var value = count == 1 ? reader.ReadInt32() : throw new FormatException("one rig source");
            return () => Native = value;
        };
        Net = new EngineNet(Channel)
        {
            Kernel = Kernel,
            Local = () => Endpoint.Local,
            Host = () => Endpoint.Host,
            LocalAddress = () => Address,
            SessionOf = address => session.Hub.Players.Find(each => AddressOf(each.Local) == address)?.Local ?? 0,
            Boundary = run => Presents?.Invoke() ?? run(),
            StepReceived = Steps.Add,
            StateReceived = (complete, epoch, body) =>
            {
                States.Add((complete, epoch));
                EngineNet.ApplyState(Kernel, complete, epoch, body);
            }
        };
        // As the bridge does: the state stream starts over with ForgeCore's world, and a new number is the kernel's next world.
        Endpoint.Node.WorldChanged += world =>
        {
            Net.WorldChanged();
            if (world.Reason is not (NetWorldReason.Checkpoint or NetWorldReason.Host) && EngineNet.WorldBase(world) > Kernel.WorldEpoch)
                Kernel.BeginWorld(EngineNet.WorldBase(world));
        };
    }

    internal ulong Player => Endpoint.Local;
    /// <summary>The address a requested step names to reach this machine's player.</summary>
    internal string Address => AddressOf(Endpoint.Local);

    private static string AddressOf(ulong player) => "slot-" + player;
}
