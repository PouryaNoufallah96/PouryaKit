# gRPC

`PouryaKit.Grpc`: a Module's gRPC services mapped on Web's conventions, a failed `Result` answered as a gRPC status with every Error in a trailer, and clients of another Host that carry the Operation Context through Web's one header codec. It references `PouryaKit.Web`, `Grpc.AspNetCore.Server` and `Grpc.Net.ClientFactory`. Namespace `PouryaKit.Grpc`. Contracts are Protobuf only.

- [The contract](#the-contract)
- [Declaring and mapping a service](#declaring-and-mapping-a-service)
- [Failures](#failures)
- [Calling another Host](#calling-another-host)
- [The loopback rule](#the-loopback-rule)
- [Testing](#testing)

## The contract

The `.proto` lives in the Module's Contracts project, which both sides compile: the Module's service base and another Host's client. Contracts reference nothing of the Kit; they take the code generator and the Protobuf runtime themselves:

```xml
<ItemGroup>
  <PackageReference Include="Google.Protobuf" />
  <PackageReference Include="Grpc.Core.Api" />
  <PackageReference Include="Grpc.Tools" PrivateAssets="all" />
</ItemGroup>
<ItemGroup>
  <Protobuf Include="Products/products.proto" />
</ItemGroup>
```

Set `option csharp_namespace` to the namespace the folder makes (`Catalog.Contracts.Products`), and name the service so its generated class clashes with no namespace (`ProductService`, not `Products`).

## Declaring and mapping a service

In the Module's Composition Root, and once in the Host after `MapPouryaKitModuleEndpoints`:

```csharp
module.AddPouryaKitGrpcService<ProductGrpcService>();

app.MapPouryaKitGrpcServices();
```

```csharp
internal sealed class ProductGrpcService(IGatewayProcessor<CatalogModule> catalog, OperationContext context)
    : ProductService.ProductServiceBase
{
    public override async Task<ProductReply> GetProduct(GetProductRequest request, ServerCallContext call) =>
        Guid.TryParse(request.ProductId, CultureInfo.InvariantCulture, out var productId)
            ? (await catalog.QueryAsync(new GetProductQuery(productId), context, call.CancellationToken))
                .ToGrpcResponse(product => new ProductReply { Id = product.Id.ToString(), Name = product.Name })
            : throw new RpcException(new Status(StatusCode.InvalidArgument, "product_id is not a Guid."));
}
```

- `MapPouryaKitGrpcServices()` maps every declared service on one route group at the root, never inside a Module's prefixed or versioned group: a gRPC method's path is its Protobuf package, service and method. The group requires an authenticated caller and carries Web's `BeginOperationPerRequest()`, so the call's Operation begins before the service is created, from the request's scope, and the service injects the `OperationContext` and passes it to its Module's Gateway, as an HTTP endpoint does ([web.md](web.md)).
- The Actor is the authenticated caller. A caller authenticated as a peer in `PouryaKitTrustedPeerOptions.Peers` continues its own Operation from the Kit headers in its call metadata; every other caller's Kit headers are ignored, as over HTTP.
- A service of a Module that also maps HTTP endpoints answers beside them; nothing of the HTTP pipeline changes.
- A Host that declares a service and never calls `MapPouryaKitGrpcServices()` fails when it starts, naming the service: a startup check, as for unmapped endpoints.

## Failures

One shape per transport (ADR 0008): HTTP answers a problem response, gRPC a status.

| Error kind | Status |
|---|---|
| Validation | `InvalidArgument` |
| Not found | `NotFound` |
| Conflict | `Aborted` |
| Forbidden | `PermissionDenied` |
| Unexpected | `Internal` |

- `result.ToGrpcResponse(response)` and `result.ToGrpcResponse(value => response)` return the response on success and throw an `RpcException` on failure. When the Errors are of several kinds the most severe decides, in HTTP's order: unexpected, forbidden, not found, conflict, validation. The status's message is the deciding Error's.
- Every Error, in order, is in the binary trailer `ResultGrpcExtensions.ErrorsTrailer` (`pouryakit-errors-bin`), a JSON array in the Kit's representation whose entries are those of an HTTP problem's `errors` member: `code`, `message` and, for a validation Error with one, `target`. A client reads it from `RpcException.Trailers`.
- A trusted peer's Kit headers the Kit cannot read answer `InvalidArgument` with the rule they broke as the message, before the service runs, where HTTP answers a 400 problem response.
- An unauthenticated call answers `Unauthenticated`, from the HTTP 401 authorization writes.
- A call to a service no Module declared answers `Unimplemented`, from routing's HTTP 404: the Kit maps no catch-all endpoint for unknown services, since it would accept every method on any two-segment path and turn a Module's 405 and 415 into 404.

## Calling another Host

On the calling Host, for each generated client, under a name of its own:

```csharp
builder.Services.AddPouryaKitGrpcClient<ProductService.ProductServiceClient>("catalog");
builder.Services.Configure<PouryaKitGrpcClientOptions>("catalog", builder.Configuration.GetSection("PouryaKit:Grpc:Clients:catalog"));
```

```json
"PouryaKit": { "Grpc": { "Clients": { "catalog": { "Address": "https://catalog.internal", "Deadline": "00:00:10" } } } }
```

```csharp
var reply = await products.GetProductAsync(
    new GetProductRequest { ProductId = id.ToString() },
    new CallOptions(cancellationToken: cancellationToken).WithOperationContext(context)
);
```

- `PouryaKitGrpcClientOptions`: `Address`, an absolute `http` or `https` address, and `Deadline`, positive, both required. The Host fails when it starts, naming the client, when either is missing or invalid.
- The client's interceptor writes the Operation Context each call is given with `WithOperationContext(context)` as Web's Kit header pairs (`OperationContextHttpHeaders.Pairs`): the Actor, the tenant, the correlation, and the calling Operation's `OperationId` as the causation. Every Kit header the call's metadata already held is removed first, so a tenant given beside a context without one never reaches the peer. A call without a context throws `InvalidOperationException` before it is sent. A typed client injects the `OperationContext` of its Operation and passes it.
- The options' `Deadline` applies to every call that sets no deadline of its own.
- Nothing of an incoming request is copied, its bearer token least of all. A peer that must authenticate this Host gets the Host's own credentials, added on the builder `AddPouryaKitGrpcClient` returns; the receiving Host lists this Host's Actor claim in its trusted peers.

## The loopback rule

A gRPC client never calls its own Host: a call between Modules of one Host goes through the Gateway, synchronously and inside the asking Operation ([modules.md](modules.md)). The Host's start fails, naming the client and the URL, when a client's address is one of the Host's configured listening URLs: `urls`, `http_ports` and `https_ports`, each `Kestrel:Endpoints:{name}:Url`, and the addresses the application adds after `Build` (`app.Urls`). An address matches on its scheme and port when its host is the same, or is loopback (`localhost`, `127.0.0.1`, `[::1]`) while the URL is loopback or listens on every address (`*`, `+`, `0.0.0.0`, `[::]`). Another host on the same port, such as a peer container on the 8080 every ASP.NET Core image listens on, is another Host and starts. This is a startup check because `app.Urls` exists only on the built application; it runs before the server binds, so an address chosen only at bind, port 0 or a `Listen` call in code, and another name for the same machine are not seen and stay this rule's.

## Testing

- Two Hosts of the application run in one test process, each built with `AppFixture.ComposeHost` and a client of the other: `ConfigurePrimaryHttpMessageHandler(() => new Http2(peer.GetTestServer().CreateHandler()))`, where `Http2` is a `DelegatingHandler` that sets the response's `Version` to the request's, since the in-memory server answers HTTP/1.1 and a gRPC client refuses that. Add the peer credentials with `AddHttpMessageHandler`.
- A plain `GrpcChannel` over the same handler sends exactly the metadata a call names, such as a raw peer's unreadable headers.
- Read a failure's Errors with `JsonDocument.Parse(failure.Trailers.Get(ResultGrpcExtensions.ErrorsTrailer)!.ValueBytes)`.
- `GrpcTests` in the Proving Sample's tests holds each case.
