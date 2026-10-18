using System.Security.Cryptography;
using System.Text;
using Billing.Quotes.Entities;
using Billing.Quotes.Features.ExpireQuote;
using Billing.Quotes.ValueObjects;
using Ledger.Contracts.Accounts.Events.V1;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Messaging;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Persistence;

namespace Billing.Quotes.Features.OpenWelcomeQuote;

// Offers every account Ledger opens a welcome quote, valid for 30 days from the opening, and schedules its expiry as
// OpenQuote does. The quote, its expiry and the delivery's Receipt commit together: a failed delivery leaves none of
// them, and a redelivered one opens nothing more.
internal sealed class OpenWelcomeQuoteIntegrationEventHandler(IRepository<Quote, QuoteId> quotes, IDelayedCommands later, OperationContext context)
    : IIntegrationEventHandler<AccountOpenedV1>
{
    private static readonly TimeSpan ValidFor = TimeSpan.FromDays(30);

    public async Task<Result> Handle(AccountOpenedV1 integrationEvent, CancellationToken cancellationToken)
    {
        var quote = Quote.Open(QuoteIdOf(context.Tenant, integrationEvent.AccountId), integrationEvent.OccurredOn + ValidFor);
        await quotes.AddAsync(quote, cancellationToken);
        later.Schedule(new ExpireQuoteCommand(quote.Id.Value), quote.ExpiresOn);
        return Result.Success();
    }

    /// <summary>
    /// The welcome quote's id: a Ledger account is one under each tenant, and Billing keeps quotes of every tenant in one
    /// table, so the id is derived from the delivered tenant and the account together.
    /// </summary>
    public static QuoteId QuoteIdOf(string? tenant, Guid accountId) =>
        new(new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{tenant}/{accountId:N}")).AsSpan(0, 16)));
}
