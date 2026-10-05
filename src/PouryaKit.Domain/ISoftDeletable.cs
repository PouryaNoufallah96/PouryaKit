namespace PouryaKit.Domain;

/// <summary>
/// Opt-in for an aggregate or entity that is kept, marked deleted, when it is removed. The Unit of Work turns the
/// removal into this mark before it saves, with the Operation's clock and the Id of its Actor; implement each as a
/// property with a private setter, which nothing in the domain calls. EF Core queries leave a deleted one out; a
/// plain SQL read says <c>is_deleted = false</c> itself. Implement it on an aggregate or an entity that is not mapped
/// as an owned type, at the root of its inheritance hierarchy or below a root that implements it too: EF Core filters
/// only those, so the model refuses to build otherwise.
/// </summary>
public interface ISoftDeletable
{
    bool IsDeleted { get; }

    DateTimeOffset? DeletedAt { get; }

    /// <summary>The Id of the Actor whose Operation removed it.</summary>
    string? DeletedBy { get; }
}
