using System.Globalization;
using Catalog.Contracts.Products;
using Grpc.Core;
using PouryaKit.Cqrs;
using PouryaKit.Grpc;

namespace Catalog.Products.Features.GetProduct;

// Created for each call in the request's scope, where the call's Operation has begun, as an HTTP endpoint's handler is.
internal sealed class ProductGrpcService(IDispatcher dispatcher) : ProductService.ProductServiceBase
{
    public override async Task<ProductReply> GetProduct(
        GetProductRequest request,
        ServerCallContext call
    ) =>
        Guid.TryParse(request.ProductId, CultureInfo.InvariantCulture, out var productId)
            ? (
                await dispatcher.QueryAsync(new GetProductQuery(productId), call.CancellationToken)
            ).ToGrpcResponse(product => new ProductReply
            {
                Id = product.Id.ToString(),
                CategoryId = product.CategoryId.ToString(),
                Name = product.Name,
            })
            : throw new RpcException(
                new Status(StatusCode.InvalidArgument, "product_id is not a Guid.")
            );
}
