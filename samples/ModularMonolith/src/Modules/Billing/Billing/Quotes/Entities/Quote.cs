using Billing.Quotes.Errors;
using Billing.Quotes.ValueObjects;
using PouryaKit.Domain;

namespace Billing.Quotes.Entities;

internal enum QuoteState
{
    Open,
    Expired,
}

// A price offered until it expires. Opening a quote schedules its expiry, which runs later, on the authority of
// whoever opened it.
internal sealed class Quote : AggregateRoot<QuoteId>, IAuditable
{
    private Quote() { }

    public QuoteState State { get; private set; }

    public DateTimeOffset ExpiresOn { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public string CreatedBy { get; private set; } = "";

    public DateTimeOffset LastChangedAt { get; private set; }

    public string LastChangedBy { get; private set; } = "";

    public static Quote Open(QuoteId id, DateTimeOffset expiresOn) =>
        new()
        {
            Id = id,
            State = QuoteState.Open,
            ExpiresOn = expiresOn,
        };

    public Result Expire()
    {
        if (State != QuoteState.Open)
        {
            return QuoteErrors.NotOpen;
        }

        State = QuoteState.Expired;
        return Result.Success();
    }
}
