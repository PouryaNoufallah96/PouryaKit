using Billing.Quotes.Entities;
using Billing.Quotes.Errors;
using Billing.Quotes.ValueObjects;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence;

namespace Billing.Quotes.Features.ExpireQuote;

// Runs when the expiry OpenQuote scheduled is due, as a continuation of the Operation that opened the quote.
internal sealed class ExpireQuoteCommandHandler(IRepository<Quote, QuoteId> quotes) : CommandHandler<ExpireQuoteCommand>
{
    public override async Task<Result> Handle(ExpireQuoteCommand command, CancellationToken cancellationToken)
    {
        var quote = await quotes.FindAsync(new QuoteId(command.QuoteId), cancellationToken);
        return quote is null ? QuoteErrors.NotFound : quote.Expire();
    }
}
