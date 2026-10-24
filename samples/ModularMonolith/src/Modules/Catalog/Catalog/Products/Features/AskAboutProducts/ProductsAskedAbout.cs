using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Catalog.Products.Features.AskAboutProducts;

// The agent's memory, in the conversation the store holds under the key: before each turn it tells the model which
// products GetProduct found in earlier turns, which outlive the history a turn sends, and after a turn that answered it
// notes those the turn found, the most recent first.
internal sealed class ProductsAskedAbout(ProductConversations conversations, string key) : AIContextProvider
{
    private const int Kept = 20;

    protected override async ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default) =>
        await conversations.RecallAsync(key, cancellationToken) is [_, ..] remembered
            ? new AIContext { Instructions = $"Earlier the customer asked about: {string.Join("; ", remembered)}." }
            : new AIContext();

    protected override async ValueTask StoreAIContextAsync(InvokedContext context, CancellationToken cancellationToken = default)
    {
        // What each GetProduct call answered: the product, as the Kit's JSON writes it, or a failed call's errors.
        string[] found =
        [
            .. context
                .ResponseMessages!.SelectMany(message => message.Contents.OfType<FunctionResultContent>())
                .Select(result => result.Result)
                .OfType<JsonElement>()
                .Where(product => product.ValueKind == JsonValueKind.Object && product.TryGetProperty("name", out _))
                .Select(product => $"{product.GetProperty("name").GetString()} ({product.GetProperty("id").GetGuid()})")
                .Reverse(),
        ];
        if (found is [])
        {
            return;
        }

        await conversations.RememberAsync(key, [.. found.Concat(await conversations.RecallAsync(key, cancellationToken)).Distinct().Take(Kept)], cancellationToken);
    }
}
