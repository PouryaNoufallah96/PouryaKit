using Billing.Quotes.Entities;
using Billing.Quotes.Features.ExpireQuote;
using Billing.Quotes.ValueObjects;
using Ledger.Contracts.Accounts.Events.V1;
using PouryaKit.Domain;
using PouryaKit.Messaging;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Persistence;

namespace Billing.Quotes.Features.OpenWelcomeQuote;

// Offers every account Ledger opens a welcome quote, under the account's id, valid for 30 days from the opening, and
// schedules its expiry as OpenQuote does. The quote, its expiry and the delivery's Receipt commit together: a failed
// delivery leaves none of them, and a redelivered one opens nothing more.
internal sealed class OpenWelcomeQuoteIntegrationEventHandler(IRepository<Quote, QuoteId> quotes, IDelayedCommands later)
    : IIntegrationEventHandler<AccountOpenedV1>
{
    private static readonly TimeSpan ValidFor = TimeSpan.FromDays(30);

    public async Task<Result> Handle(AccountOpenedV1 integrationEvent, CancellationToken cancellationToken)
    {
        var quote = Quote.Open(new QuoteId(integrationEvent.AccountId), integrationEvent.OccurredOn + ValidFor);
        await quotes.AddAsync(quote, cancellationToken);
        later.Schedule(new ExpireQuoteCommand(integrationEvent.AccountId), quote.ExpiresOn);
        return Result.Success();
    }
}
