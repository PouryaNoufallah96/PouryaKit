using System.Globalization;
using System.Reflection;

namespace PouryaKit.Domain;

/// <summary>
/// The base of every typed ID, over <see cref="Guid"/>, <see cref="long"/>, <see cref="int"/> or <see cref="string"/>.
/// Declare one per aggregate: <c>public sealed record ProductId(Guid Value) : TypedId&lt;Guid&gt;(Value);</c>
/// Two IDs are equal only when their types and their values are equal.
/// </summary>
public abstract record TypedId<TValue>
    where TValue : notnull
{
    private static readonly bool Supported =
        typeof(TValue) == typeof(Guid)
        || typeof(TValue) == typeof(long)
        || typeof(TValue) == typeof(int)
        || typeof(TValue) == typeof(string);

    protected TypedId(TValue value)
    {
        if (!Supported)
        {
            throw new NotSupportedException(
                $"A typed ID wraps a Guid, long, int or string, not {typeof(TValue).Name}."
            );
        }

        if (value is null || (value is Guid guid && guid == Guid.Empty) || (value is string text && string.IsNullOrWhiteSpace(text)))
        {
            throw new ArgumentException($"{GetType().Name} cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public TValue Value { get; }

    public sealed override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Value}");
}

/// <summary>
/// Creates typed IDs without per-ID code. EF Core, Dapper and JSON registration build on
/// <see cref="GetValueType"/> to find typed IDs and <see cref="From{TId,TValue}"/> to create them.
/// </summary>
public static class TypedId
{
    /// <summary>A new Guid ID, time-ordered (version 7) so that primary-key inserts stay sequential.</summary>
    public static TId New<TId>()
        where TId : TypedId<Guid> => From<TId, Guid>(Guid.CreateVersion7());

    /// <summary>The typed ID <typeparamref name="TId"/> holding <paramref name="value"/>.</summary>
    public static TId From<TId, TValue>(TValue value)
        where TId : TypedId<TValue>
        where TValue : notnull => Factory<TId, TValue>.Create(value);

    /// <summary>The value type a concrete typed ID wraps, or null when <paramref name="type"/> is not one.</summary>
    public static Type? GetValueType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type.IsAbstract)
        {
            return null;
        }

        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(TypedId<>))
            {
                return current.GetGenericArguments()[0];
            }
        }

        return null;
    }

    // The constructor is looked up once per ID type; ConstructorInvoker does not wrap the
    // constructor's own exceptions, so an empty value still surfaces as ArgumentException.
    private static class Factory<TId, TValue>
        where TId : TypedId<TValue>
        where TValue : notnull
    {
        public static readonly Func<TValue, TId> Create = Build();

        private static Func<TValue, TId> Build()
        {
            var constructor = typeof(TId).GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                [typeof(TValue)]
            );
            if (typeof(TId).IsAbstract || constructor is null)
            {
                return _ =>
                    throw new InvalidOperationException(
                        $"{typeof(TId).Name} needs a constructor that takes a single {typeof(TValue).Name}."
                    );
            }

            var invoker = ConstructorInvoker.Create(constructor);
            return value => (TId)invoker.Invoke(value);
        }
    }
}
