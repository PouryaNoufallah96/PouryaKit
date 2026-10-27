using Catalog.Products.Entities;
using Catalog.Products.Errors;
using Catalog.Products.Features.GetProduct;
using Catalog.Products.ValueObjects;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PouryaKit.AI;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Modularity;
using PouryaKit.Persistence;

namespace Catalog.Products.Features.AskAboutProducts;

#region ask-about-products-handler
// The sample's agent: an Agent Framework agent over the Host's one chat client, whose allow-list, iteration cap and
// guardrails hold for every call it makes to the model, with one tool, GetProduct through Catalog's Gateway. The run
// executes inside this authorized command, so each look-up continues it, as the asking Actor and tenant. The Actor's
// conversation in the tenant, its session and its memory, is read as the turn starts and written by this command's
// Unit of Work, which holds its transaction while the model answers.
internal sealed class AskAboutProductsCommandHandler(
    IChatClient chat,
    IGatewayProcessor<CatalogModule> catalog,
    IRepository<ProductConversation, ProductConversationId> conversations,
    OperationContext context
) : CommandHandler<AskAboutProductsCommand, string>
{
    private const string Instructions =
        "You answer questions about a shop's catalog. Look up each product a question names by its id with the GetProduct tool before you answer about it.";

    // The last messages of the conversation each turn sends the model, its tool calls and their results left out.
    private const int HistoryLength = 20;

    public override async Task<Result<string>> Handle(AskAboutProductsCommand command, CancellationToken cancellationToken)
    {
        var getProduct = catalog.Tool(
            "GetProduct",
            "Finds one product of the catalog by its id: its id, its category's id and its name.",
            context,
            (ProductToFind product) => new GetProductQuery(product.ProductId)
        );
        var store = new ProductConversations(conversations, context);
        var key = ProductConversations.KeyOf(context);
#pragma warning disable MEAI001 // Microsoft.Extensions.AI 10.11's reducers are experimental.
        var agent = chat.AsAIAgent(
            new ChatClientAgentOptions
            {
                ChatOptions = new() { Instructions = Instructions, Tools = [getProduct] },
                ChatHistoryProvider = new InMemoryChatHistoryProvider(new() { ChatReducer = new MessageCountingChatReducer(HistoryLength) }),
                AIContextProviders = [new ProductsAskedAbout(store, key)],
            }
        );
#pragma warning restore MEAI001
        var session = await agent.LoadSessionAsync(store, key, cancellationToken);
        var answer = await agent.RunAsync(command.Question, session, cancellationToken: cancellationToken);
        // A refused turn fails, so the Unit of Work keeps nothing of it, what the memory noted included: a refused question
        // kept in the session would be sent again, and refused again, with every later one.
        if (answer.FinishReason == ChatFinishReason.ContentFilter)
        {
            return ProductErrors.QuestionRefused(answer.Text);
        }

        await agent.SaveSessionAsync(store, key, session, cancellationToken);
        return answer.Text;
    }
}
#endregion
