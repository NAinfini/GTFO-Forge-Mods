namespace ForgeCore;

/// <summary>A message stamped with the world epoch of the host that sent it (see <see cref="NetChannel.Epoch"/>). Implement it
/// explicitly over the existing field, so the wire layout stays as it was.</summary>
public interface INetEpoch
{
    ulong Epoch { get; set; }
}
