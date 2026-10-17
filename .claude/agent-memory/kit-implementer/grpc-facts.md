---
name: grpc-facts
description: PouryaKit.Grpc on Grpc.AspNetCore.Server and Grpc.Net.ClientFactory 2.84.0 - the metapackage trap, the unknown-service catch-all, gRPC over TestServer, convention order, per-call context, the loopback check's moment
metadata:
  type: project
---

## gRPC services and clients (#134, 2026-10-18; Grpc 2.84.0, Google.Protobuf 3.36.2, ASP.NET Core 10.0.12)

- **`Grpc.AspNetCore` carries `Grpc.Tools` with `include="All"`** (its nuspec), so a library on the metapackage pushes protoc's build targets into every consumer. The Kit package references `Grpc.AspNetCore.Server` and `Grpc.Net.ClientFactory`; the project with the `.proto` (Catalog.Contracts) takes `Google.Protobuf`, `Grpc.Core.Api` and `Grpc.Tools` (`PrivateAssets="all"`). `ReferencesOfContracts` rejects only `PouryaKit*` and Modules, so that passes; `PackageGraph.KeptOut` keeps `Grpc.Tools` out of the package.
- **`MapGrpcService` adds a catch-all `{unimplementedService}/{unimplementedMethod:grpcunimplemented}` with no method metadata**: routing's method policy then routes every method on any two-segment path to it, the constraint fails, and a Module's 405 and 415 become 404 (`WebTests` routing rows, only in the full run). `AddGrpc(o => o.IgnoreUnknownServices = true)` skips it (decompiled `ServiceRouteBuilder<T>.CreateUnimplementedEndpoints`).
- **gRPC over TestServer:** `GrpcChannel` or `ConfigurePrimaryHttpMessageHandler(() => new Http2(server.CreateHandler()))`, where `Http2` sets `response.Version = request.Version`; the in-memory server answers 1.1 and the client refuses it. A trailers-only answer written by hand (200, `application/grpc`, `grpc-status`, `grpc-message` escaped with `Uri.EscapeDataString`) is read by Grpc.Net.Client with the exact message.
- **A `Finally` added on a group after `BeginOperationPerRequest()` wraps outside Web's wrapper**, so it catches the `BadHttpRequestException` Web's internal `KitBadRequestException` derives from; Grpc.AspNetCore's delegate turns every service exception into a status itself.
- **Per-call context with no slot in `CallOptions`:** the extension copies the headers into a new `Metadata` and keys the context on that instance in a `ConditionalWeakTable`; the interceptor looks it up and writes `OperationContextHttpHeaders.Pairs`.
- **`IStartupCheck` runs in `StartingAsync`, before Kestrel binds**: `IServerAddressesFeature` holds only `app.Urls`; read `urls`, `http_ports`, `https_ports` and `Kestrel:Endpoints:*:Url` from configuration, parse with `BindingAddress.Parse` (handles `*` and `+`). A `ValidateOnStart` failure comes before it.
- `SharedJsonTests` pins the callers of `UsePouryaKitJson` by type name in two lists; a new caller is added there, and its test names are cited by acceptance and ADR 0006, so keep them.
- A sample `.proto` is not packed by `PouryaKit.Templates` unless its csproj globs `Modules/**/*.proto` (approved line, #134); a gRPC service that calls the Gateway is a Gateway-bound adapter: the monolith excludes it (`**/*GrpcService.cs`) and has its own on `IDispatcher`.
- **Review of #134 (dual, two highs):** an every-address listener (`http_ports`, `*`, `0.0.0.0`) must match only a loopback client, or every peer on the shared container port 8080 fails the start; a writer of Kit headers removes the codec's whole name list (`OperationContextHttpHeaders.Names`, internal), not the names `Pairs` returned, or a stale tenant survives beside a tenantless context. Cross-package reuse of Web's internals goes through `InternalsVisibleTo` in Web's csproj (as Cqrs opens to Web), never a copy.

**Why:** each cost a full-suite run or would have shipped a broken HTTP pipeline or package closure.
**How to apply:** any ticket that touches PouryaKit.Grpc, adds a transport on Web's convention, or tests a second Host over an in-memory server. Related: [[web-security-and-openapi-facts]], [[templates-packaging-and-publication-facts]], [[docs-verification-tools]].
