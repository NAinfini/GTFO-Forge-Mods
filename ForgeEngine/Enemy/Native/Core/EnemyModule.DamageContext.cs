using System;

namespace ForgeEnemy.Native;

internal sealed partial class EnemyModule
{
    private IntPtr _requestedDamageReceiver;
    private int _requestedDamageKind = NoDamageKind;

    // Adapter transport is independent of the requested semantics. Scoped to the exact receiver and native
    // call, including exceptional exits; other recipients keep their own native entry's observed kind.
    internal int EffectiveDamageKind(Dam_EnemyDamageBase receiver)
        => _requestedDamageReceiver == receiver.Pointer ? _requestedDamageKind : _damageKind;

    private NativeDamageRequest BeginNativeDamageRequest(Dam_EnemyDamageBase receiver, int kind)
    {
        var scope = new NativeDamageRequest(this, _requestedDamageReceiver, _requestedDamageKind);
        _requestedDamageReceiver = receiver.Pointer;
        _requestedDamageKind = kind;
        return scope;
    }

    private readonly struct NativeDamageRequest : IDisposable
    {
        private readonly EnemyModule _owner;
        private readonly IntPtr _receiver;
        private readonly int _kind;
        internal NativeDamageRequest(EnemyModule owner, IntPtr receiver, int kind)
        { _owner = owner; _receiver = receiver; _kind = kind; }
        public void Dispose()
        { _owner._requestedDamageReceiver = _receiver; _owner._requestedDamageKind = _kind; }
    }
}
