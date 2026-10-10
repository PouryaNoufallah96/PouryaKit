using System.Runtime.CompilerServices;

namespace PouryaKit.Domain;

/// <summary>An object with an identity. Two entities are equal when their types and IDs are equal.</summary>
/// <remarks>
/// Equality and hashing use the current ID. Do not change an entity's ID while it is in a hash-based collection
/// using entity equality. For database-generated IDs, add the entity after its ID is assigned, or use
/// <see cref="ReferenceEqualityComparer.Instance"/> when the collection tracks object instances across persistence.
/// With reference equality, different instances remain distinct even when their IDs are equal.
/// </remarks>
public abstract class Entity<TId>
    where TId : notnull
{
    /// <summary>The entity's identity, which its equality compares.</summary>
    public TId Id { get; protected init; } = default!;

    /// <summary>True when both are null, the same instance, or entities of one type with equal IDs.</summary>
    public static bool operator ==(Entity<TId>? left, Entity<TId>? right) => Equals(left, right);

    /// <summary>True when the two are not equal as entities.</summary>
    public static bool operator !=(Entity<TId>? left, Entity<TId>? right) => !Equals(left, right);

    /// <inheritdoc />
    public override bool Equals(object? obj) =>
        obj is Entity<TId> other
        && (ReferenceEquals(this, other) || (other.GetType() == GetType() && Id is not null && Id.Equals(other.Id)));

    /// <inheritdoc />
    public override int GetHashCode() => Id is null ? RuntimeHelpers.GetHashCode(this) : HashCode.Combine(GetType(), Id);

    /// <summary>
    /// A failed Result with the rule's Error when <paramref name="rule"/> is broken; otherwise a success.
    /// </summary>
    protected static Result CheckRule(IBusinessRule rule) => BusinessRule.Check(rule);
}
