# PouryaKit.Realtime provenance

Adopted implementations in `PouryaKit.Realtime`. Format and rules: [README](README.md).

The push on the delivery's transaction (`src/PouryaKit.Realtime/RealtimePublisher.cs`), the listener over PostgreSQL's `LISTEN` (`src/PouryaKit.Realtime/RealtimeListener.cs`), the clients keyed by tenant and Channel (`src/PouryaKit.Realtime/RealtimeClients.cs`) and the connection check (`src/PouryaKit.Realtime/RealtimeConnectionCheck.cs`) are new to the Kit, decided by spec #115 (R1) and recorded in ADR 0012 (ticket #133).

## Package dependencies

- `Npgsql` 10.0.3, `PostgreSQL`, the latest stable on nuget.org on 2026-10-18 (ticket #133), already pinned for `PouryaKit.Persistence.Dapper`, so a Host has one driver: the listener's dedicated connection and its `Notification` event, and the parameters of the push. On `net10.0` it brings `Microsoft.Extensions.Logging.Abstractions` 10.0.0, `MIT`.

`PouryaKit.Realtime` also references the ASP.NET Core shared framework (`Microsoft.AspNetCore.App`), for the server-sent events result and the hosted service, and `PouryaKit.Web` and `PouryaKit.Persistence.EFCore`, as spec #115 places it; `PackageGraphTests` holds it to those and `LicenceTests` to its closure. Its private MinVer build reference is recorded in [shared private build tools](README.md#shared-private-build-tools).

## The SSE endpoint

- **Kit files:** `src/PouryaKit.Realtime/RealtimeEndpointRouteBuilderExtensions.cs`, `src/PouryaKit.Realtime/RealtimeClients.cs`
- **Upstream:** Ultrabusiness (`ultrabusiness_server_microservices`, a private repository with no public URL)
- **Commit:** d685de64c22dcb9be5b49206c6d9699da4ef6302
- **Upstream paths:** `src/BuildingBlocks/BuildingBlocks.SSE/Extensions/SseEndpointExtensions.cs` (line 58: `TypedResults.ServerSentEvents` over an async stream of a channel reader; lines 73 to 110: an initial event, then a wait that ends the stream on the client's cancellation); read at commit `3cca42ed1d9d2f2440cdeb18ad3ac30146218fe9`, where the cited file is byte-identical to the pin `d685de6` (`git diff` empty, checked 2026-10-18), line 58 holding the call the plan names
- **Licence:** unconfirmed
- **Adoption:** pattern
- **Ticket:** #133
- **Intentional differences:**
  - Upstream reads each event from its `SseConnectionManager`, a dictionary of the connections of one process, registered as a singleton (`Services/SseConnectionManager.cs:22`, `Extensions/SseServiceExtensions.cs:31`), so a push reaches only the clients of the instance that made it. The Kit's instance hears every committed push from PostgreSQL and keeps only its own clients, keyed by tenant and Channel.
  - Upstream keys a connection by its user; the Kit by the request's tenant and the Channels its endpoint's rule admits for the Actor, refused with a problem response before anything streams.
  - The data is written as a string the push already holds in the Host's HTTP JSON, where upstream serializes each event with options of its own.
  - The stream ends when the Host stops as well as when the client disconnects; a client a full buffer behind is disconnected; a `gap` event follows a reconnected listener.
  - No `Enabled` switch answering 503: a Host that does not register the package maps no endpoint.
