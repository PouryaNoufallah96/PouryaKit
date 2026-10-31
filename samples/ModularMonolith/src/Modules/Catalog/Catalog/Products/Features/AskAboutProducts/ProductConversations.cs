using System.Text.Json;
using Catalog.Products.Data.Specifications;
using Catalog.Products.Entities;
using Catalog.Products.ValueObjects;
using PouryaKit.AI;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence;

namespace Catalog.Products.Features.AskAboutProducts;

// Catalog's store of its agent's conversations, the session and the memory of each in one aggregate, which the command's
// Unit of Work writes: nothing here saves. A turn reads its conversation once, as it starts, and writes what it read, so
// of two turns of one conversation at once the second to commit fails with UnitOfWorkErrors.ConcurrencyConflict, from
// the aggregate's version or, for two first turns, from the unique index on the key, which CatalogModule maps, and keeps
// nothing; read again before the write, the other turn's committed row would be overwritten.
internal sealed class ProductConversations(
    IRepository<ProductConversation, ProductConversationId> conversations,
    OperationContext context
) : IAgentSessionStore
{
    private readonly Dictionary<string, ProductConversation?> _read = [];

    // The Actor's conversation in the Operation's tenant, each part escaped, so no two Actors or tenants share one.
    public static string KeyOf(OperationContext context) =>
        $"{Uri.EscapeDataString(context.Tenant!)}/{context.Actor.Kind}/{Uri.EscapeDataString(context.Actor.Id)}";

    public async Task<JsonElement?> FindAsync(string key, CancellationToken cancellationToken) =>
        (await ConversationAsync(key, cancellationToken))?.Session is { } session
            ? JsonElement.Parse(session)
            : null;

    public async Task SaveAsync(
        string key,
        JsonElement session,
        CancellationToken cancellationToken
    ) => (await OfAsync(key, cancellationToken)).Keep(session.GetRawText());

    public async Task<IReadOnlyList<string>> RecallAsync(
        string key,
        CancellationToken cancellationToken
    ) =>
        (await ConversationAsync(key, cancellationToken))?.Memory is { } memory
            ? JsonSerializer.Deserialize<string[]>(memory)!
            : [];

    public async Task RememberAsync(
        string key,
        IReadOnlyList<string> memory,
        CancellationToken cancellationToken
    ) => (await OfAsync(key, cancellationToken)).Remember(JsonSerializer.Serialize(memory));

    private async Task<ProductConversation> OfAsync(string key, CancellationToken cancellationToken)
    {
        if (await ConversationAsync(key, cancellationToken) is { } conversation)
        {
            return conversation;
        }

        var started = ProductConversation.Start(
            TypedId.New<ProductConversationId>(),
            key,
            context.Tenant!
        );
        await conversations.AddAsync(started, cancellationToken);
        return _read[key] = started;
    }

    private async Task<ProductConversation?> ConversationAsync(
        string key,
        CancellationToken cancellationToken
    )
    {
        if (!_read.TryGetValue(key, out var conversation))
        {
            _read[key] = conversation = await conversations.FindAsync(
                new ProductConversationKeyed(key),
                cancellationToken
            );
        }

        return conversation;
    }
}
