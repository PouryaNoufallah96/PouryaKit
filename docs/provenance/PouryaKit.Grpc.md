# PouryaKit.Grpc provenance

Adopted implementations in `PouryaKit.Grpc`. Format and rules: [README](README.md).

The service side builds on Web's own members (`BeginOperationPerRequest`, `OperationContextHttpHeaders.Pairs`, ticket #119), the `Result` to status mapping reuses Web's `ResultHttpExtensions`, its deciding Error and its entry shape (ADR 0008), and the loopback check is the Kit's own `IStartupCheck` shape, so they have no record here.

## Package dependencies

Checked on nuget.org on 2026-10-18 (ticket #134); licence expressions from each package's nuspec. None is reciprocal or commercial (ADR 0002).

- `Grpc.AspNetCore.Server` 2.84.0, `Apache-2.0`, the latest stable, with a `net10.0` asset. It brings `Grpc.Net.Common` and `Grpc.Core.Api` 2.84.0, both `Apache-2.0`.
- `Grpc.Net.ClientFactory` 2.84.0, `Apache-2.0`, the latest stable. It brings `Grpc.Net.Client` 2.84.0, `Apache-2.0`, and `Microsoft.Extensions.Http` 8.0.1, `MIT`, which the shared framework's 10.0 line supersedes.
- Not the `Grpc.AspNetCore` 2.84.0 metapackage the plan's row names: its nuspec carries `Grpc.Tools` with `include="All"`, so a Kit package on it would put protoc's build targets into every consumer. A project that compiles a `.proto` references `Grpc.Tools` 2.84.0 (`Apache-2.0`) itself with private assets, beside `Google.Protobuf` 3.36.2 (`BSD-3-Clause`) and `Grpc.Core.Api` 2.84.0 for the generated code, as the Proving Sample's `Catalog.Contracts` does. `PackageGraph` keeps `Grpc.Tools` out of the package's closure.

Its private MinVer build reference is recorded in [shared private build tools](README.md#shared-private-build-tools).

## gRPC client registration

- **Kit files:** `src/PouryaKit.Grpc/Clients/GrpcClientServiceCollectionExtensions.cs`, `src/PouryaKit.Grpc/Clients/OperationContextInterceptor.cs`, `src/PouryaKit.Grpc/Clients/OperationContextCallOptions.cs`
- **Upstream:** Ultrabusiness (`ultrabusiness_server_microservices`, a private repository with no public URL)
- **Commit:** a85eacd4db5e1af9b8349d49350be12397f24402
- **Upstream paths:** `src/BuildingBlocks/MainArchitecture/Presentation/MainArchitecture.API/Configurations/Extensions/GrpcExtensions.cs` (lines 68 to 89: `AddGrpcClientService`); read at the local checkout's working tree, not at this commit
- **Licence:** unconfirmed
- **Adoption:** pattern
- **Ticket:** #134
- **Intentional differences:**
  - Kept: one registration per generated client through `AddGrpcClient<TClient>`, with one interceptor added to it.
  - The interceptor writes the Operation Context the call is given (`WithOperationContext`) as Web's one header codec returns it (`OperationContextHttpHeaders.Pairs`), and nothing else. Upstream's `Interceptors/GrpcExceptionInterceptor.cs` (lines 48 to 81) forwards the incoming request's bearer token, its company, branch, owner, request id and language headers, a second header vocabulary, and logs each request's payload; none of it is taken.
  - The address and the deadline come from options the Host binds from configuration and the Kit validates at start, read inside the client factory's callback; upstream takes the address as a string argument and the deadline from a constant of the interceptor. A call's own deadline is kept.
  - No primary handler of the Kit's: upstream accepts any server certificate in Development (`DangerousAcceptAnyServerCertificateValidator`); the Kit leaves the handler to the Host.
  - No exception interceptor: a failed call is the caller's `RpcException`, whose status and Errors' trailer the service side wrote (`ResultGrpcExtensions`).
