namespace PouryaKit.Domain;

/// <summary>
/// Opt-in for an aggregate or entity whose creation and last change are recorded. The Unit of Work stamps these
/// before it saves, from the Operation's clock and the Id of the Operation's Actor; implement each as a property with
/// a private setter, which nothing in the domain calls. A change to anything an aggregate owns counts as a change of
/// its root: an owned type, or a child entity the aggregate holds in a navigation, changed, added or removed. A child
/// moved from one aggregate to another changes both. A child the aggregate reaches by no navigation is not part of it.
/// </summary>
public interface IAuditable
{
    DateTimeOffset CreatedAt { get; }

    /// <summary>The Id of the Actor whose Operation created it.</summary>
    string CreatedBy { get; }

    /// <summary>When it was last changed; on creation, when it was created.</summary>
    DateTimeOffset LastChangedAt { get; }

    /// <summary>The Id of the Actor whose Operation last changed it.</summary>
    string LastChangedBy { get; }
}
