using Billing.Quotes.Entities;
using Billing.Quotes.Features.ExpireQuote;
using Billing.Quotes.ValueObjects;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Persistence;

namespace Billing.Quotes.Features.OpenQuote;

#region open-quote-handler
// Opens a quote and schedules its expiry: the Unit of Work writes the expiry with the quote, in one commit, and the
// message store keeps it until it is due.
internal sealed class OpenQuoteCommandHandler(
    IRepository<Quote, QuoteId> quotes,
    IDelayedCommands later,
    TimeProvider time
) : CommandHandler<OpenQuoteCommand>
{
    public override async Task<Result> Handle(
        OpenQuoteCommand command,
        CancellationToken cancellationToken
    )
    {
        var quote = Quote.Open(new QuoteId(command.QuoteId), time.GetUtcNow() + command.ValidFor);
        await quotes.AddAsync(quote, cancellationToken);
        later.Schedule(new ExpireQuoteCommand(command.QuoteId), quote.ExpiresOn);
        return Result.Success();
    }
}
#endregion
