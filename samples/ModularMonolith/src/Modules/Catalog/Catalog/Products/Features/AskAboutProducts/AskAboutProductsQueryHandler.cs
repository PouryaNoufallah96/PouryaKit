using Catalog.Products.Features.GetProduct;
using Microsoft.Extensions.AI;
using PouryaKit.AI;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Modularity;

namespace Catalog.Products.Features.AskAboutProducts;

// The sample's agent: a model answers the question with one tool, GetProduct through Catalog's Gateway. The run executes
// inside this authorized Operation, so each look-up the model asks for continues it, as the asking Actor and tenant.
internal sealed class AskAboutProductsQueryHandler(IChatClient chat, IGatewayProcessor<CatalogModule> catalog, OperationContext context)
    : QueryHandler<AskAboutProductsQuery, string>
{
    private const string Instructions =
        "You answer questions about a shop's catalog. Look up each product a question names by its id with the GetProduct tool before you answer about it.";

    public override async Task<Result<string>> Handle(AskAboutProductsQuery query, CancellationToken cancellationToken)
    {
        var getProduct = catalog.Tool(
            "GetProduct",
            "Finds one product of the catalog by its id: its id, its category's id and its name.",
            context,
            (ProductToFind product) => new GetProductQuery(product.ProductId)
        );
        var answer = await chat.GetResponseAsync(
            [new(ChatRole.System, Instructions), new(ChatRole.User, query.Question)],
            new ChatOptions { Tools = [getProduct] },
            cancellationToken
        );
        return answer.Text;
    }
}
