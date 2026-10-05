namespace PouryaKit.Domain;

/// <summary>
/// The base of a Domain Event. Its identity (a version 7 Guid) and its UTC time are assigned once,
/// when the event is created, and survive serialization, so a redelivered event is recognisable.
/// </summary>
public abstract record DomainEvent
{
    protected DomainEvent()
    {
        var now = DateTimeOffset.UtcNow;
        Id = Guid.CreateVersion7(now);
        OccurredOn = now;
    }

    public Guid Id { get; init; }

    public DateTimeOffset OccurredOn
    {
        get;
        init => field = value.ToUniversalTime();
    }
}
