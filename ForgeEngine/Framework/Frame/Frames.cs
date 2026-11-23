using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace ForgeEngine.Framework;

/// <summary>
/// One runtime value type's physical tag. The two leading states are "no value at all" and "an explicit null";
/// every entry after them is the same declaration order as <see cref="Wire.RuntimeValueTypes"/>,
/// so <c>ValueKind - 2</c> is that array's index. That correspondence is asserted once per plan load
/// (<see cref="RuntimeFrames.ValidateTables"/>) instead of being maintained as a third list.
/// </summary>
public enum ValueKind : byte
{
    /// <summary>The slot was never written: an optional port the plan did not wire, or an unevaluated pure memo.</summary>
    Missing = 0,
    /// <summary>An explicit JSON null that <c>RuntimeJson.ValidateValue</c> accepted on a nullable port.</summary>
    Null = 1,
    Boolean = 2,
    Integer = 3,
    Number = 4,
    String = 5,
    /// <summary>Three component slots, counted as one segment head carrying x plus its y and z neighbours. A
    /// collection of vector3 keeps the head separate and gives every element these same three slots.</summary>
    Vector3 = 6,
    Entity = 7,
    Enum = 8,
    /// <summary>A handle value: the 12 identity bytes kept in <see cref="FrameValue.Handle"/> plus the creating
    /// provider's registration index in the slot's integer field. A handle is moved between frames, never folded,
    /// compared or decoded as a plain value.</summary>
    Handle = 9,
    /// <summary>A runtime resource reference: the resource kind's index in <see cref="Wire.ResourceKinds"/>
    /// in the head slot's integer field, and the id as a string slot one slot later. The two slots are one value,
    /// which is why a resource is the one type whose single form is already wider than one slot.</summary>
    Resource = 10,
    /// <summary>The row index of the event a dispatch is running for. The envelope fields are supplied by the kernel
    /// in their own fixed order; a slot carries only which row it is.</summary>
    Event = 11,
    /// <summary>A result row is a region of its own declared field slots, so this kind never tags a slot: it is the
    /// descriptor's kind for the port whose width is the row, and the row's first field sits where the port starts.</summary>
    Result = 12,
    /// <summary>A value was supplied, but its wire form does not match the slot's declared type. Typed command
    /// readers return null/false for it while <c>Present</c> remains true, so malformed input is never mistaken for
    /// an omitted optional value.</summary>
    Invalid = byte.MaxValue,
}

/// <summary>
/// A frame slot: a fixed 24-byte value cell whose single live field is selected by <see cref="Kind"/>.
/// <see cref="Count"/> is the element count of a segmented kind — 3 for a vector3, 0..256 for a collection, 0 for
/// every single value — and the elements follow the head slot one after another. A string keeps its UTF-8 bytes in
/// the owning <see cref="FrameSpace"/>'s string region, never inline. An enum keeps only its member index here: the
/// port's value set lives in the descriptor, so no second copy of it can drift.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct FrameValue
{
    [FieldOffset(0)] public ValueKind Kind;
    /// <summary>Segment length, kept in the two bytes next to <see cref="Kind"/> so that a handle's 12 identity
    /// bytes and its provider index both fit in the same 24-byte cell.</summary>
    [FieldOffset(2)] public ushort Count;
    /// <summary>A handle's identity, bit-for-bit the same three components as <see cref="FrameEntity"/>; it sits
    /// four bytes earlier in the cell so the provider index in the integer field never aliases its local slot.</summary>
    [FieldOffset(4)] public FrameHandle Handle;
    [FieldOffset(8)] public FrameEntity Entity;
    [FieldOffset(16)] public double Number;
    [FieldOffset(16)] public long Integer;
    [FieldOffset(16)] public bool Boolean;
    [FieldOffset(16)] public int EnumIndex;
    [FieldOffset(16)] public int StringOffset;
    [FieldOffset(20)] public int StringLength;

    public static FrameValue Of(bool value) => new() { Kind = ValueKind.Boolean, Boolean = value };
    public static FrameValue Of(long value) => new() { Kind = ValueKind.Integer, Integer = value };
    public static FrameValue Of(double value) => new() { Kind = ValueKind.Number, Number = value };
    public static FrameValue Of(int enumIndex) => new() { Kind = ValueKind.Enum, EnumIndex = enumIndex };
    public static FrameValue Of(in FrameEntity entity) => new() { Kind = ValueKind.Entity, Entity = entity };
    /// <summary>The provider index is the registration-order slot of the provider that created the handle; the
    /// identity bytes say which of that provider's handles it is.</summary>
    public static FrameValue Of(in FrameHandle handle, int provider)
        => new() { Kind = ValueKind.Handle, Handle = handle, Integer = provider };
    public static FrameValue Of(int stringOffset, int stringLength)
        => new() { Kind = ValueKind.String, StringOffset = stringOffset, StringLength = stringLength };
    /// <summary>A resource's head slot: the resource kind's index in <see cref="Wire.ResourceKinds"/>.
    /// The id is the string slot written next to it by <see cref="FrameWriter.SetResource"/>, which is what makes one
    /// resource two slots wide and never one.</summary>
    public static FrameValue OfResource(int kindIndex)
        => new() { Kind = ValueKind.Resource, Integer = kindIndex };
    /// <summary>The dispatch's own event row. The row itself is the kernel's; a slot only names which one.</summary>
    public static FrameValue OfEvent(long rowIndex) => new() { Kind = ValueKind.Event, Integer = rowIndex };
    /// <summary>The explicit null of a nullable port; distinct from <see cref="Missing"/>.</summary>
    public static FrameValue Null() => new() { Kind = ValueKind.Null };
}

/// <summary>
/// The three identity components of one entity reference, in the order entity identity is always carried.
/// 12 bytes keep it inside a single <see cref="FrameValue"/>; the epochs use 16 of the 64 bits the public
/// <see cref="EntityReference"/> carries because a frame slot is fixed width, and both epochs are bounded by
/// <c>int.MaxValue</c> at the boundary that creates them (world start and life allocation).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 12)]
internal readonly struct FrameEntity
{
    [FieldOffset(0)] public readonly int WorldEpoch;
    [FieldOffset(4)] public readonly int LifeEpoch;
    [FieldOffset(8)] public readonly int Local;

    public FrameEntity(int worldEpoch, int lifeEpoch, int local)
    { WorldEpoch = worldEpoch; LifeEpoch = lifeEpoch; Local = local; }

    public override string ToString() => WorldEpoch + ":" + LifeEpoch + ":" + Local;
}

/// <summary>
/// The identity half of one handle value, bit-for-bit the same three components as <see cref="FrameEntity"/>:
/// the world it was created in, the life epoch that owns it (for an entity-scoped handle) or the pool
/// generation that makes a recycled slot distinguishable, and the local slot. A handle's remaining identity
/// components — its kind and its lifetime — belong to the port descriptor, and its provider index sits in the
/// integer field of the same <see cref="FrameValue"/>. Nothing here is a value the runtime compares: a handle
/// is only ever moved to the step that consumes it, which is where it is validated once.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 12)]
internal readonly struct FrameHandle
{
    [FieldOffset(0)] public readonly int WorldEpoch;
    [FieldOffset(4)] public readonly int LifeEpoch;
    [FieldOffset(8)] public readonly int Local;

    public FrameHandle(int worldEpoch, int lifeEpoch, int local)
    { WorldEpoch = worldEpoch; LifeEpoch = lifeEpoch; Local = local; }

    public override string ToString() => WorldEpoch + ":" + LifeEpoch + ":" + Local;
}

/// <summary>
/// One runtime resource reference as a frame carries it: the kind's index in the shared resource-kind table plus
/// the id the plan compiled. The kind stays an index for the same reason an enum's member does — the port's own
/// descriptor names the kind, so no second copy of the vocabulary can drift — and the id is only decoded where a
/// provider is actually asked to resolve the reference, never while a frame is moved.
/// </summary>
internal readonly struct FrameResource
{
    internal FrameResource(int kindIndex, string id) { KindIndex = kindIndex; Id = id; }

    /// <summary>The resource kind's index in <see cref="Wire.ResourceKinds"/>.</summary>
    public int KindIndex { get; }
    public string Id { get; }
    public override string ToString() => KindIndex + ":" + Id;
}

/// <summary>
/// The pool address of an entity inside the runtime's own provider namespace: <see cref="Provider"/> is the
/// registration-order slot of the owning entity namespace, <see cref="Local"/> the slot inside it. It never
/// leaves the kernel — domain code keeps the public, string-keyed <see cref="EntityReference"/>, whose pool
/// slots are recycled — so nothing outside can hold an index that later names a different entity.
/// </summary>
internal readonly struct EntityId
{
    public EntityId(int provider, int local) { Provider = provider; Local = local; }
    public int Provider { get; }
    public int Local { get; }
    public override string ToString() => Provider + ":" + Local;
}

/// <summary>
/// Read-only view over one already-laid-out frame region. A frame is opened at a base slot of the storage
/// array and addressed by the offsets the load-time descriptors computed, never by port name. Every accessor
/// is a direct array read: no lookup, no dictionary, no allocation. A kind mismatch is a caller programming
/// error (contract I-DIAG's SDK-misuse class), not a value-domain rejection, so it throws here.
/// </summary>
internal readonly struct Frame
{
    private readonly FrameValue[]? storage;
    private readonly ReadOnlyMemory<byte> strings;

    /// <summary>The frame whose every slot reads back as <see cref="ValueKind.Missing"/>.</summary>
    public static Frame Empty { get; } = new Frame(Array.Empty<FrameValue>(), 0, ReadOnlyMemory<byte>.Empty);

    /// <summary>Opens a frame over an existing storage array, e.g. a <c>PlanFrames</c> constant pool or a queue arena.</summary>
    internal Frame(FrameValue[] storage, int baseSlot, ReadOnlyMemory<byte> strings)
    {
        this.storage = storage;
        this.strings = strings;
        Base = baseSlot;
    }

    public int Base { get; }

    public ValueKind Kind(int slot) => Value(slot).Kind;
    public bool IsMissing(int slot) => Value(slot).Kind == ValueKind.Missing;
    /// <summary>True for an explicit null or an unwritten optional slot; false for every real value.</summary>
    public bool IsNull(int slot) { var kind = Value(slot).Kind; return kind is ValueKind.Null or ValueKind.Missing; }
    /// <summary>Element count of a segmented port: 0 for a single value, N for a collection, and 3 for a vector3 —
    /// whose head slot is the value itself, so its count is the component count rather than an element count.</summary>
    public int Count(int slot) => Value(slot).Count;

    public bool Boolean(int slot) { var value = Expect(slot, ValueKind.Boolean); return value.Boolean; }
    public long Integer(int slot) { var value = Expect(slot, ValueKind.Integer); return value.Integer; }
    public double Number(int slot) { var value = Expect(slot, ValueKind.Number); return value.Number; }
    /// <summary>The member index; the port's value set (hence the member name) belongs to the descriptor.</summary>
    public int EnumIndex(int slot) { var value = Expect(slot, ValueKind.Enum); return value.EnumIndex; }
    /// <summary>Reads the three components of a vector3, whose head slot carries x and whose next two slots carry
    /// y and z: one value, three slots.</summary>
    public void Vector3(int slot, out double x, out double y, out double z)
    {
        var value = Expect(slot, ValueKind.Vector3);
        if (value.Count != RuntimeFrames.VectorWidth) throw Misuse(slot, "a vector3 segment of three slots");
        x = value.Number; y = Value(slot + 1).Number; z = Value(slot + 2).Number;
    }
    public FrameEntity Entity(int slot)
    {
        var value = Value(slot);
        if (value.Kind != ValueKind.Entity || value.Count != 0) throw Misuse(slot, "a single entity");
        return value.Entity;
    }
    /// <summary>Nullable entity port: false for a null or unwritten slot instead of throwing.</summary>
    public bool TryEntity(int slot, out FrameEntity entity)
    {
        var value = Value(slot);
        if (value.Kind != ValueKind.Entity || value.Count != 0) { entity = default; return false; }
        entity = value.Entity; return true;
    }
    /// <summary>One element of a segmented port, whatever the element kind is: the direct way to move a collection
    /// without a scratch buffer. <paramref name="index"/> counts elements, not slots, so a vector3 element spans
    /// three slots and every other element one. A single value has no element.</summary>
    public FrameValue Element(int slot, int index)
    {
        var value = Value(slot);
        if (index < 0 || index >= value.Count) throw Misuse(slot, "an element index inside a segment's count");
        return Value(slot + 1 + index * RuntimeFrames.ElementWidth(value.Kind));
    }
    public bool Boolean(int slot, int index) => Value(ElementSlot(slot, index, ValueKind.Boolean)).Boolean;
    public long Integer(int slot, int index) => Value(ElementSlot(slot, index, ValueKind.Integer)).Integer;
    public double Number(int slot, int index) => Value(ElementSlot(slot, index, ValueKind.Number)).Number;
    public FrameEntity Entity(int slot, int index) => Value(ElementSlot(slot, index, ValueKind.Entity)).Entity;
    /// <summary>One member index of an enum collection.</summary>
    public int EnumIndex(int slot, int index) => Value(ElementSlot(slot, index, ValueKind.Enum)).EnumIndex;
    /// <summary>One element of a string collection, decoded the same way a single string slot is.</summary>
    public string String(int slot, int index) => String(ElementSlot(slot, index, ValueKind.String));
    /// <summary>One component triple of a vector3 collection; the element's own head slot carries x.</summary>
    public void Vector3(int slot, int index, out double x, out double y, out double z)
    {
        var element = ElementSlot(slot, index, ValueKind.Vector3);
        var value = Value(element);
        if (value.Count != RuntimeFrames.VectorWidth) throw Misuse(element, "a vector3 element of three slots");
        x = value.Number; y = Value(element + 1).Number; z = Value(element + 2).Number;
    }
    /// <summary>One handle element, with the registration index of the provider that created it.</summary>
    public FrameHandle Handle(int slot, int index, out int provider)
    {
        var value = Value(ElementSlot(slot, index, ValueKind.Handle));
        provider = (int)value.Integer;
        return value.Handle;
    }
    /// <summary>A single handle: its identity half plus the registration index of the provider that created it.</summary>
    public FrameHandle Handle(int slot, out int provider)
    {
        var value = Expect(slot, ValueKind.Handle);
        provider = (int)value.Integer;
        return value.Handle;
    }
    /// <summary>Nullable handle port: false for a null or unwritten slot instead of throwing.</summary>
    public bool TryHandle(int slot, out FrameHandle handle, out int provider)
    {
        var value = Value(slot);
        if (value.Kind != ValueKind.Handle) { handle = default; provider = -1; return false; }
        handle = value.Handle; provider = (int)value.Integer; return true;
    }
    /// <summary>A runtime resource reference: the kind's index in the shared resource-kind table plus the id in the
    /// string slot written next to it. Decoding the id allocates, so this is called where a provider is asked to
    /// resolve the reference, not where a frame is moved.</summary>
    public FrameResource Resource(int slot)
    {
        var value = Expect(slot, ValueKind.Resource);
        return new FrameResource((int)value.Integer, String(slot + 1));
    }
    /// <summary>Nullable resource port: false for a null or unwritten slot instead of throwing.</summary>
    public bool TryResource(int slot, out FrameResource resource)
    {
        var value = Value(slot);
        if (value.Kind != ValueKind.Resource) { resource = default; return false; }
        resource = new FrameResource((int)value.Integer, String(slot + 1)); return true;
    }
    /// <summary>The event row this dispatch is running for; the envelope is the kernel's, not the frame's.</summary>
    public long EventRow(int slot) => Expect(slot, ValueKind.Event).Integer;
    /// <summary>One field of a result row. <paramref name="fieldOffset"/> is the field's slot offset inside the row,
    /// which the row's descriptor carries, so the returned frame is the field's own region: one slot for every field
    /// type the row validator admits, three for a vector3 column.</summary>
    public Frame ResultRow(int slot, int fieldOffset) => new(storage!, Base + slot + fieldOffset, strings);
    /// <summary>Decodes the slot's UTF-8 bytes. Allocates by nature, so it is called only where a string is
    /// actually needed (the receipt/log boundary), never to move a value between frames.</summary>
    public string String(int slot)
    {
        var value = Expect(slot, ValueKind.String);
        if (value.StringOffset < 0 || value.StringLength < 0 || value.StringOffset + value.StringLength > strings.Length)
            throw Misuse(slot, "a string region inside the frame's string area");
        return value.StringLength == 0 ? "" : Encoding.UTF8.GetString(strings.Span.Slice(value.StringOffset, value.StringLength));
    }

    /// <summary>Whether a string slot holds exactly these UTF-8 bytes, compared in place: the way a reader asks for
    /// one known word without decoding the slot into a string.</summary>
    public bool StringEquals(int slot, ReadOnlySpan<byte> utf8)
    {
        var value = Value(slot);
        return value.Kind == ValueKind.String && value.StringLength == utf8.Length
            && strings.Span.Slice(value.StringOffset, value.StringLength).SequenceEqual(utf8);
    }

    /// <summary>The slot of one element of a collection: the head must carry the element kind and a count the index
    /// falls inside, and the element itself must already have been written. A reader that finds anything else is
    /// reading a frame that does not match its descriptor, which is a caller error, not a value rejection.</summary>
    private int ElementSlot(int slot, int index, ValueKind kind)
    {
        var head = Expect(slot, kind);
        if (index < 0 || index >= head.Count) throw Misuse(slot, "an element index inside the segment's count");
        var element = slot + 1 + index * RuntimeFrames.ElementWidth(kind);
        if (Value(element).Kind != kind) throw Misuse(element, "a written " + kind + " element");
        return element;
    }

    private FrameValue Value(int slot)
    {
        if (storage is null) { if (slot == 0) return default; throw Misuse(slot, "a slot inside the frame"); }
        if (slot < 0 || Base + slot >= storage.Length) throw Misuse(slot, "a slot inside the frame");
        return storage[Base + slot];
    }
    private FrameValue Expect(int slot, ValueKind kind)
    {
        var value = Value(slot);
        if (value.Kind != kind) throw Misuse(slot, kind.ToString());
        return value;
    }
    private static InvalidOperationException Misuse(int slot, string expected)
        => new("Frame slot " + slot + " does not hold " + expected + ".");
}

/// <summary>
/// Writes one frame region. Only slots are addressable — a port name was already turned into an offset when the
/// plan was loaded — and segments are bounded by the stride the descriptor reserved for that port, so a
/// handler that outgrows its frame is refused (<c>result-row-budget</c>) instead of overwriting its neighbour.
/// </summary>
internal ref struct FrameWriter
{
    private readonly FrameValue[] storage;
    private readonly FrameStrings strings;
    private readonly int baseSlot;
    private readonly int span;

    internal FrameWriter(FrameValue[] storage, int baseSlot, int span, FrameStrings strings)
    {
        this.storage = storage; this.baseSlot = baseSlot; this.span = span; this.strings = strings;
    }

    /// <summary>Marks one range of the region unwritten, so a slot a previous write left behind cannot be read as
    /// the value this write is about. The string region is append-only and shared with every other frame of the same
    /// space, so a string already written stays readable for its owner.</summary>
    public void Clear(int slot, int width)
    {
        Need(slot, width);
        for (var index = 0; index < width; index++) storage[baseSlot + slot + index] = default;
    }

    public void Set(int slot, bool value) => Write(slot, FrameValue.Of(value));
    public void Set(int slot, long value) => Write(slot, FrameValue.Of(value));
    public void Set(int slot, double value) => Write(slot, FrameValue.Of(value));
    public void Set(int slot, in FrameEntity entity) => Write(slot, FrameValue.Of(entity));
    /// <summary>Writes a handle and the registration index of its creating provider into one slot.</summary>
    public void SetHandle(int slot, in FrameHandle handle, int provider)
    {
        if (provider < 0) throw Budget(slot, "a handle with a provider index");
        Write(slot, FrameValue.Of(handle, provider));
    }
    /// <summary>Enum members are indices into the port's declared value set; the set itself never enters the frame.</summary>
    public void SetEnum(int slot, int memberIndex, int memberCount)
    {
        if (memberIndex < 0 || memberIndex >= memberCount) throw Budget(slot, "an enum member index inside the port's value set");
        Write(slot, FrameValue.Of(memberIndex));
    }
    public void SetNull(int slot) => Write(slot, FrameValue.Null());
    /// <summary>Writes a vector3 as three slots: the head carries x and its two neighbours carry y and z.</summary>
    public void SetVector3(int slot, double x, double y, double z)
    {
        Need(slot, RuntimeFrames.VectorWidth);
        storage[baseSlot + slot] = new FrameValue { Kind = ValueKind.Vector3, Count = RuntimeFrames.VectorWidth, Number = x };
        storage[baseSlot + slot + 1] = FrameValue.Of(y);
        storage[baseSlot + slot + 2] = FrameValue.Of(z);
    }
    /// <summary>Opens a collection slot: the head carries the element kind and how many elements the writer is
    /// about to place, and the whole segment is reserved now, so an element write can never reach a neighbour's
    /// slots. Only an element kind with a value form of its own can be a segment; an event and a result row are a
    /// dispatch identity and a row, and neither has a collection form.</summary>
    public void SetSegment(int slot, ValueKind kind, int count)
    {
        if (!RuntimeFrames.Segmented(kind)) throw Budget(slot, "a segment of " + kind);
        if (count < 0 || count > Wire.MaxSetWidth) throw Budget(slot, "a segment count inside the runtime's set width");
        Need(slot, 1 + count * RuntimeFrames.ElementWidth(kind));
        storage[baseSlot + slot] = new FrameValue { Kind = kind, Count = (ushort)count };
    }
    public void SetBoolean(int slot, int index, bool value) => SetElement(slot, index, ValueKind.Boolean, FrameValue.Of(value));
    public void SetInteger(int slot, int index, long value) => SetElement(slot, index, ValueKind.Integer, FrameValue.Of(value));
    public void SetNumber(int slot, int index, double value) => SetElement(slot, index, ValueKind.Number, FrameValue.Of(value));
    public void SetEntity(int slot, int index, in FrameEntity entity) => SetElement(slot, index, ValueKind.Entity, FrameValue.Of(entity));
    /// <summary>One element of an enum collection: a member index into the port's declared set, like a single enum.</summary>
    public void SetEnum(int slot, int index, int memberIndex, int memberCount)
    {
        if (memberIndex < 0 || memberIndex >= memberCount) throw Budget(slot, "an enum member index inside the port's value set");
        SetElement(slot, index, ValueKind.Enum, FrameValue.Of(memberIndex));
    }
    /// <summary>One handle element: the identity triple plus the registration index of its creating provider.</summary>
    public void SetHandle(int slot, int index, in FrameHandle handle, int provider)
    {
        if (provider < 0) throw Budget(slot, "a handle with a provider index");
        SetElement(slot, index, ValueKind.Handle, FrameValue.Of(handle, provider));
    }
    /// <summary>Writes one element of a string collection into the frame's string region.</summary>
    public void SetString(int slot, int index, ReadOnlySpan<char> value)
        => storage[baseSlot + ElementSlot(slot, index, ValueKind.String)] = StringSlot(slot, value);
    /// <summary>Writes one component triple of a vector3 collection, at the element's own three slots.</summary>
    public void SetVector3(int slot, int index, double x, double y, double z)
    {
        var element = ElementSlot(slot, index, ValueKind.Vector3);
        storage[baseSlot + element] = new FrameValue { Kind = ValueKind.Vector3, Count = RuntimeFrames.VectorWidth, Number = x };
        storage[baseSlot + element + 1] = FrameValue.Of(y);
        storage[baseSlot + element + 2] = FrameValue.Of(z);
    }
    /// <summary>Writes an entity collection through the same segment API every other collection uses;
    /// <paramref name="stride"/> is the descriptor's reserved width, so a set that outgrows its port is refused
    /// before a single element is written.</summary>
    public void SetEntities(int slot, ReadOnlySpan<FrameEntity> entities, int stride)
    {
        if (entities.Length > RuntimeFrames.SetCapacity(stride, ValueKind.Entity)) throw Budget(slot, "an entity set inside the port's reserved width");
        SetSegment(slot, ValueKind.Entity, entities.Length);
        for (var index = 0; index < entities.Length; index++) SetEntity(slot, index, entities[index]);
    }
    /// <summary>A runtime resource reference: the kind's index in the shared kind table goes into the head slot and
    /// the id into the string slot next to it. The two slots are one value, which is why a resource port reserves
    /// two slots even though every other single value reserves one.</summary>
    public void SetResource(int slot, int kindIndex, ReadOnlySpan<char> resourceId)
    {
        if (kindIndex < 0) throw Budget(slot, "a resource with a kind index");
        Need(slot, RuntimeFrames.ResourceWidth);
        storage[baseSlot + slot + 1] = StringSlot(slot, resourceId);
        storage[baseSlot + slot] = FrameValue.OfResource(kindIndex);
    }
    /// <summary>The row index of the event this dispatch runs for; the envelope belongs to the kernel.</summary>
    public void SetEvent(int slot, long rowIndex)
    {
        if (rowIndex < 0) throw Budget(slot, "an event row index");
        Write(slot, FrameValue.OfEvent(rowIndex));
    }
    /// <summary>Writes a string into the frame's string region and points the slot at those bytes.</summary>
    public void SetString(int slot, ReadOnlySpan<char> value) => Write(slot, StringSlot(slot, value));

    /// <summary>One element of a collection: the head must be that kind's segment and the index must fall inside
    /// the count the writer declared. Writing an element of a segment the slot does not hold is a caller error.</summary>
    private int ElementSlot(int slot, int index, ValueKind kind)
    {
        Need(slot, 1);
        var head = storage[baseSlot + slot];
        if (head.Kind != kind) throw Budget(slot, "a " + kind + " segment head");
        if (index < 0 || index >= head.Count) throw Budget(slot, "an element index inside the segment's declared count");
        var element = slot + 1 + index * RuntimeFrames.ElementWidth(kind);
        Need(element, RuntimeFrames.ElementWidth(kind));
        return element;
    }
    private void SetElement(int slot, int index, ValueKind kind, FrameValue value)
        => storage[baseSlot + ElementSlot(slot, index, kind)] = value;
    private FrameValue StringSlot(int slot, ReadOnlySpan<char> value)
    {
        if (value.Length > Wire.PlanJson.MaxStringValueLength) throw Budget(slot, "a string inside the runtime's string budget");
        var (offset, length) = strings.Add(value);
        return FrameValue.Of(offset, length);
    }

    private ref FrameValue At(int slot)
    {
        Need(slot, 1);
        return ref storage[baseSlot + slot];
    }
    private void Write(int slot, FrameValue value) { At(slot) = value; }
    private void Need(int slot, int width)
    {
        if (slot < 0 || width < 1 || slot + width > span) throw Budget(slot, "a write inside the port's reserved width");
    }
    private static RuntimeContractException Budget(int slot, string expected)
        => new("result-row-budget", "Frame slot " + slot + " cannot hold " + expected + ".");
}

/// <summary>
/// An append-only UTF-8 string region with exact-match deduplication, shared by every frame of one
/// <see cref="FrameSpace"/>. Nothing is ever reclaimed in place: the dispatch arena resets between advances
/// and the plan constant region is written once at load, which is what lets a slot keep an (offset, length)
/// pair instead of owning the bytes.
/// </summary>
internal sealed class FrameStrings
{
    private readonly byte[] bytes;
    private readonly int capacity;
    /// <summary>The deduplication index: chains of entries by hash, kept in two arrays the region owns for its whole
    /// life. A rewind empties the chains and reuses both arrays, so writing a string into a frame allocates nothing
    /// once the arrays have reached the number of distinct strings one activation writes.</summary>
    private int[] heads = new int[64];
    private (int Hash, int Offset, int Length, int Next)[] entries = new (int, int, int, int)[32];
    private int entryCount;

    internal FrameStrings(int capacity) { this.capacity = capacity; bytes = new byte[capacity]; }

    internal int Count { get; private set; }
    internal ReadOnlyMemory<byte> Memory => new(bytes, 0, Count);

    internal (int Offset, int Length) Add(ReadOnlySpan<char> value)
    {
        var encoded = Encoding.UTF8.GetByteCount(value);
        RuntimeJson.Require(encoded <= RuntimeFrames.MaximumStringBytes, "frame-string-budget", "A frame string exceeds 4096 bytes.");
        Span<byte> candidate = stackalloc byte[encoded];
        Encoding.UTF8.GetBytes(value, candidate);
        // A bucket selector for deduplication, not a content identity.
        var hash = string.GetHashCode(value);
        for (var at = heads[hash & (heads.Length - 1)]; at != 0; at = entries[at - 1].Next)
        {
            ref var entry = ref entries[at - 1];
            if (entry.Hash == hash && entry.Length == encoded && candidate.SequenceEqual(bytes.AsSpan(entry.Offset, entry.Length)))
                return (entry.Offset, entry.Length);
        }
        RuntimeJson.Require(Count + encoded <= capacity, "frame-string-budget", "The frame string region is exhausted.");
        var offset = Count;
        candidate.CopyTo(bytes.AsSpan(offset));
        Count += encoded;
        if (entryCount == entries.Length) Array.Resize(ref entries, entries.Length * 2);
        if (entryCount >= heads.Length) Rehash();
        var head = hash & (heads.Length - 1);
        entries[entryCount] = (hash, offset, encoded, heads[head]);
        heads[head] = ++entryCount;
        return (offset, encoded);
    }

    /// <summary>Doubles the chain heads once the entries outnumber them, relinking every entry by its kept hash.</summary>
    private void Rehash()
    {
        heads = new int[heads.Length * 2];
        for (var index = 0; index < entryCount; index++)
        {
            var head = entries[index].Hash & (heads.Length - 1);
            entries[index].Next = heads[head];
            heads[head] = index + 1;
        }
    }

    /// <summary>Rewinds the region for its arena's next tick or advance; the arena is the only owner.</summary>
    internal void Reset(int retainedBytes = 0)
    {
        RuntimeJson.Require(retainedBytes >= 0 && retainedBytes <= Count, "frame-string-budget", "Invalid retained string region.");
        Count = retainedBytes;
        if (entryCount == 0) return;
        Array.Clear(heads, 0, heads.Length);
        entryCount = 0;
    }
}

/// <summary>
/// The slot storage one owner hands out regions from: a plan's constant pool, the queue arena, the dispatch
/// arena or the tick arena. Regions are fixed-size blocks assigned by the load-time descriptors, so a frame
/// never moves and never grows; <see cref="SlotCount"/> is a high-water mark used for the budget assertions.
/// </summary>
internal sealed class FrameSpace
{
    internal FrameSpace(int slotCapacity, int stringCapacity)
    {
        Slots = new FrameValue[slotCapacity];
        SlotCapacity = slotCapacity;
        StringCapacity = stringCapacity;
        Strings = new FrameStrings(stringCapacity);
    }

    internal FrameValue[] Slots { get; private set; }
    internal FrameStrings Strings { get; }
    internal int SlotCapacity { get; private set; }
    internal int StringCapacity { get; }
    internal int SlotCount { get; set; }
    /// <summary>Bytes this space may occupy at its high-water mark: slots at 24 bytes each, plus string bytes.</summary>
    internal long ByteCount => (long)SlotCapacity * RuntimeFrames.SlotBytes + StringCapacity;

    /// <summary>Room for more constant slots. Only the plan-static pool grows, once, while it is being filled.</summary>
    internal void Grow(int slotCapacity)
    {
        if (slotCapacity <= SlotCapacity) return;
        var grown = new FrameValue[slotCapacity];
        Array.Copy(Slots, grown, SlotCount);
        Slots = grown;
        SlotCapacity = slotCapacity;
    }
}

internal static class RuntimeFrames
{
    /// <summary><see cref="FrameValue"/> is 24 bytes by construction and 8-byte aligned, so a segment is a
    /// plain byte range and the whole frame region can be measured without asking the runtime for a size.</summary>
    internal const int SlotBytes = 24;

    /// <summary>A vector3 is three number slots; every other implemented value type is one.</summary>
    internal const int VectorWidth = 3;
    /// <summary>One resource is two slots — the kind index and the id's string slot — because a reference is a
    /// pair and a single 24-byte cell cannot hold both without aliasing its own number.</summary>
    internal const int ResourceWidth = 2;
    internal const int MaximumStringBytes = Wire.PlanJson.MaxStringValueLength * 4;

    /// <summary>The slots one value of a runtime type occupies: 3 for a vector3, 1 for every other type.</summary>
    internal static int ValueWidth(string type) => RuntimeGraphContracts.FrameValueType(type) == "vector3" ? VectorWidth : 1;

    /// <summary>The slots one element of a segmented kind occupies: 3 for a vector3 element, 2 for a resource (its
    /// kind index and its id string) and 1 for every other element. It is <see cref="ValueWidth"/> for the kinds
    /// that have a value form, and never asked of a kind that has none.</summary>
    internal static int ElementWidth(ValueKind kind) => kind == ValueKind.Vector3 ? VectorWidth : kind == ValueKind.Resource ? ResourceWidth : 1;

    /// <summary>The reserved width of a collection port: one head slot carrying the count, then
    /// <see cref="Wire.MaxSetWidth"/> elements at the element type's own width.</summary>
    internal static int SegmentWidth(ValueKind kind) => 1 + Wire.MaxSetWidth * ElementWidth(kind);

    /// <summary>The largest element count a port of this reserved width holds; a width of N holds the head slot and
    /// (N - 1) / elementWidth elements.</summary>
    internal static int SetCapacity(int width, ValueKind kind) => width <= 1 ? 0 : (width - 1) / ElementWidth(kind);

    /// <summary>Whether a collection port can hold each kind, indexed by kind: the members of
    /// <see cref="Wire.CollectionValueTypes"/>.</summary>
    private static readonly bool[] SegmentedKinds =
        Wire.RuntimeValueTypes.Select(type => Wire.CollectionValueTypes.Contains(type)).Prepend(false).Prepend(false).ToArray();
    internal static bool Segmented(ValueKind kind) => (int)kind < SegmentedKinds.Length && SegmentedKinds[(int)kind];

    /// <summary>Asserted at every plan load: <c>ValueKind - 2</c> is the index of the port type in
    /// <see cref="Wire.RuntimeValueTypes"/>. Without this, a value kind could silently mean a different type than
    /// the plan's own layout index does. That every value type is also a wire port type is a property of the wire
    /// table itself, refused where the table is generated. A result row is the one wire type with a frame form
    /// that is not a value kind: it is a region of its own declared field slots, and <c>policy</c> is the one that
    /// still has no frame form at all.</summary>
    internal static void ValidateTables()
    {
        for (var index = 0; index < Wire.RuntimeValueTypes.Length; index++)
            RuntimeJson.Require(string.Equals(((ValueKind)(index + 2)).ToString(), Wire.RuntimeValueTypes[index], StringComparison.OrdinalIgnoreCase),
                "frame-kind-table", Wire.RuntimeValueTypes[index]);
    }

    /// <summary>The physical tag of one wire port type; only an implemented runtime value type can be laid out. A
    /// parameter-only type takes the tag of the value type it is carried as.</summary>
    internal static ValueKind KindOf(string type)
    {
        var index = Array.IndexOf(Wire.RuntimeValueTypes, RuntimeGraphContracts.FrameValueType(type));
        RuntimeJson.Require(index >= 0, "unsupported-port", type);
        return (ValueKind)(index + 2);
    }
}
