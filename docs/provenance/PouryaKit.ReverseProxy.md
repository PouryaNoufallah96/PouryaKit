# PouryaKit.ReverseProxy provenance

Adopted implementations in `PouryaKit.ReverseProxy`. Format and rules: [README](README.md).

The liveness probe every cluster gets, `src/PouryaKit.ReverseProxy/LivenessProbe.cs`, is new to the Kit: a YARP configuration filter that turns on each cluster's active health check at the service defaults' `/alive` (ticket #126).

## Package dependencies

Checked on nuget.org on 2026-10-18 (ticket #126); licence expressions from each package's nuspec. None is reciprocal or commercial (ADR 0002), and `LicenceTests` checks the whole closure.

- `Yarp.ReverseProxy` 2.3.0, `MIT`, the latest stable; its newest asset is `net8.0`, which runs on .NET 10, and it brings `System.IO.Hashing` 8.0.0, `MIT`.
- `Microsoft.Extensions.ServiceDiscovery.Yarp` 10.10.0, `MIT`, with a `net10.0` asset, on the Kit's `Microsoft.Extensions.ServiceDiscovery` 10.10.0 line (10.11.0 is the latest; the line moves as one). It brings `Microsoft.Extensions.ServiceDiscovery` and `Microsoft.Extensions.ServiceDiscovery.Abstractions` 10.10.0, both `MIT`, and `Yarp.ReverseProxy` 2.3.0.

The package also references the ASP.NET Core shared framework (`Microsoft.AspNetCore.App`) and no Kit package. Its private MinVer build reference is recorded in [shared private build tools](README.md#shared-private-build-tools).

## Routes and clusters from configuration, destinations by service discovery

- **Kit files:** `src/PouryaKit.ReverseProxy/ReverseProxyHostApplicationBuilderExtensions.cs`
- **Upstream:** https://github.com/PouryaNoufallah96/cybernod
- **Commit:** bae8ac3b7e46f09e9375647786d03391a500876a
- **Upstream paths:** `src/API/Cybernod.ApiGateway.Yarp/Program.cs` (lines 40 to 54)
- **Licence:** MIT
- **Adoption:** pattern
- **Ticket:** #126
- **Intentional differences:**
  - No transform: upstream adds a `TraceId` header with a new id to every forwarded request, so each hop starts an identity of its own. The Kit keeps YARP's default propagator, which sends the destination the W3C context of the proxy's own span in the client's trace.
  - No `AddHttpForwarderWithServiceDiscovery`: the routes' forwarder resolves its destinations through `AddServiceDiscoveryDestinationResolver` alone, and the endpoint providers are the Host's service defaults'.
  - Each cluster probes its destinations' `/alive` unless its configuration says otherwise, where upstream probes nothing.
  - Ultrabusiness's gateway (`src/ApiGateways/ApiGateway.Yarp/Configurations/Extensions/HostExtensions.cs`, lines 34 to 60, at `a85eacd`, ownership unconfirmed) has the same shape and the same random trace header; nothing is taken from it.
