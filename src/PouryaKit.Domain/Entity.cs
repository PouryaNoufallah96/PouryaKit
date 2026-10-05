using System.Runtime.CompilerServices;

namespace PouryaKit.Domain;

/// <summary>An object with an identity. Two entities are equal when their types and IDs are equal.</summary>
public abstract class Entity<TId>
    where TId : notnull
{
    public TId Id { get; protected init; } = default!;

    public static bool operator ==(Entity<TId>? left, Entity<TId>? right) => Equals(left, right);

    public static bool operator !=(Entity<TId>? left, Entity<TId>? right) => !Equals(left, right);

    public override bool Equals(object? obj) =>
        obj is Entity<TId> other
        && (ReferenceEquals(this, other) || (other.GetType() == GetType() && Id is not null && Id.Equals(other.Id)));

    public override int GetHashCode() => Id is null ? RuntimeHelpers.GetHashCode(this) : HashCode.Combine(GetType(), Id);

    protected static Result CheckRule(IBusinessRule rule) => BusinessRule.Check(rule);
}
