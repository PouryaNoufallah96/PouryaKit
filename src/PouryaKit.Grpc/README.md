![PouryaKit.Grpc](https://raw.githubusercontent.com/PouryaNoufallah96/pouryakit-public/assets-v1/assets/covers/PouryaKit.Grpc.png)

[![NuGet](https://img.shields.io/nuget/vpre/PouryaKit.Grpc?style=flat-square&logo=nuget&color=615fff)](https://www.nuget.org/packages/PouryaKit.Grpc)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)
![License: MIT](https://img.shields.io/badge/license-MIT-615fff?style=flat-square)

# PouryaKit.Grpc

gRPC on the same conventions as the Kit's HTTP endpoints. The `.proto` contract lives in the Module's Contracts, a service is declared in its Composition Root and mapped once in the Host, and each call runs through the Module's Gateway with the Operation Context the call began with. A failed Result answers a gRPC status chosen as HTTP chooses its status, with every Error in a trailer.

## Install

```bash
dotnet add package PouryaKit.Grpc --prerelease
```

## When to use it

- Another Host asks a Module questions over gRPC rather than HTTP.
- You write a Module's gRPC service from a Protobuf contract.
- A client must read every Error of a failed call, not only its status.
- A Host calls another Host with a client that carries the Operation Context and a deadline.

It builds on [`PouryaKit.Web`](https://www.nuget.org/packages/PouryaKit.Web), whose authentication and Operation Context it reuses.

## Quick start

Declare the service in the Composition Root and map the services once in the Host:

```csharp
module.AddPouryaKitGrpcService<ProductGrpcService>();
app.MapPouryaKitGrpcServices();
```

A service runs each call through the Gateway, as the Proving Sample's does:

```csharp
internal sealed class ProductGrpcService(IGatewayProcessor<CatalogModule> catalog, OperationContext context)
    : ProductService.ProductServiceBase
{
    public override async Task<ProductReply> GetProduct(GetProductRequest request, ServerCallContext call) =>
        Guid.TryParse(request.ProductId, CultureInfo.InvariantCulture, out var productId)
            ? (await catalog.QueryAsync(new GetProductQuery(productId), context, call.CancellationToken)).ToGrpcResponse(product =>
                new ProductReply
                {
                    Id = product.Id.ToString(),
                    CategoryId = product.CategoryId.ToString(),
                    Name = product.Name,
                }
            )
            : throw new RpcException(new Status(StatusCode.InvalidArgument, "product_id is not a Guid."));
}
```

Another Host registers a client that carries the Operation Context under a name of its own, `Client` here, as the Kit's tests do. Configure the `PouryaKitGrpcClientOptions` of that name with its `Address` and `Deadline`, or the Host fails when it starts:

```csharp
services.AddPouryaKitGrpcClient<ProductService.ProductServiceClient>(Client);
```

## What you get

- Each call an Operation, authenticated as an endpoint is.
- A failed Result as a gRPC status, with every Error in a trailer a client can read.
- Clients that carry the Operation Context in their call metadata, with a deadline from validated options.
- A Host that fails when it starts if a declared service was never mapped.

> [!WARNING]
> gRPC without TLS needs an HTTP/2-only listener: a listener that serves both protocols without TLS negotiates no HTTP/2, so the Host needs one for gRPC beside the one REST clients reach.

## Learn more

- [gRPC](https://pouryakit.vercel.app/docs/capabilities/grpc)
- [Declaring and mapping a service](https://pouryakit.vercel.app/docs/capabilities/grpc#declaring-and-mapping-a-service)
- [Failures](https://pouryakit.vercel.app/docs/capabilities/grpc#failures)
- [Calling another Host](https://pouryakit.vercel.app/docs/capabilities/grpc#calling-another-host)
