
namespace ForgeVisual;

/// <summary>Whether a particle system that is a pool of <c>capacity</c> particles takes one more. Below its budget every request is
/// taken. At the budget a request of priority above 0 is taken too (Unity recycles the oldest particle of a full system, so the pool
/// never grows past its capacity); priority 0 is refused and the caller counts it. A capacity of 0 means no budget was given.</summary>
internal static class ParticleBudget
{
    internal static bool Admits(int live, int capacity, int priority) => capacity <= 0 || live < capacity || priority > 0;
}
