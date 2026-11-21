using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq.Expressions;

namespace ForgeDevelopment.Native;

// Lazy, per concrete Build declaring type. Called only on the diagnostic main thread.
internal sealed class GenerationJobContext
{
    private static readonly Dictionary<Type, GenerationJobContext> Types = new();
    private readonly Func<IntPtr, (object? Geomorph, object? Area, object? Zone)>? _read;

    private GenerationJobContext(Type type)
    {
        var geomorph = type.GetProperty("m_geomorph");
        var area = type.GetProperty("m_area");
        var zone = type.GetProperty("m_zone");
        if (geomorph == null && area == null && zone == null) return;
        var constructor = type.GetConstructor(new[] { typeof(IntPtr) });
        if (constructor == null) throw new MissingMethodException(type.FullName, ".ctor(IntPtr)");
        var pointer = Expression.Parameter(typeof(IntPtr), "pointer");
        var view = Expression.Variable(type, "view");
        Expression Read(PropertyInfo? property) => property == null ? Expression.Constant(null, typeof(object))
            : Expression.Convert(Expression.Property(view, property), typeof(object));
        var tuple = typeof(ValueTuple<object, object, object>).GetConstructor(new[] { typeof(object), typeof(object), typeof(object) })!;
        _read = Expression.Lambda<Func<IntPtr, (object?, object?, object?)>>(Expression.Block(new[] { view },
            Expression.Assign(view, Expression.New(constructor, pointer)),
            Expression.New(tuple, Read(geomorph), Read(area), Read(zone))), pointer).Compile();
    }

    internal static GenerationJobContext For(Type type)
    {
        if (!Types.TryGetValue(type, out var context)) Types.Add(type, context = new(type));
        return context;
    }

    internal (object? Geomorph, object? Area, object? Zone) Read(IntPtr pointer)
    {
        return _read == null ? default : _read(pointer);
    }
}
