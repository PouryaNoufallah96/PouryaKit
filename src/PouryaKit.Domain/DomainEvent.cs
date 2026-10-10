namespace PouryaKit.Domain;

/// <summary>
/// The base of a Domain Event. Its identity (a version 7 Guid) and its UTC time are assigned once,
/// when the event is created, from one reading of <see cref="DomainClock"/>, and survive
/// serialization, so a redelivered event is recognisable.
/// </summary>
public abstract record DomainEvent
{
    /// <summary>
    /// Creates the Domain Event and assigns its <see cref="Id"/> and <see cref="OccurredOn"/> from one reading of
    /// <see cref="DomainClock"/>.
    /// </summary>
    protected DomainEvent()
    {
        var now = DomainClock.Current.GetUtcNow();
        Id = Guid.CreateVersion7(now);
        OccurredOn = now;
    }

    /// <summary>The Domain Event's identity, which every redelivery of it keeps.</summary>
    public Guid Id { get; init; }

    /// <summary>When the Domain Event was raised, in UTC.</summary>
    public DateTimeOffset OccurredOn
    {
        get;
        init => field = value.ToUniversalTime();
    }
}
