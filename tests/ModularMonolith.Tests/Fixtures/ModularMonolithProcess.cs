namespace ModularMonolith.Tests.Fixtures;

// ModularMonolith.Host as its own process, as HostProcess runs one, on <connectionString>: it authenticates the bearer
// tokens of TestIssuer, trusts AppFixture.TrustedPeer as a peer Host, and logs each entry as one line of JSON with its
// scopes, so a test reads which Operation it ran. Its listener speaks HTTP/2 only: without TLS no negotiation picks
// HTTP/2, so Kestrel's default listener of both versions speaks HTTP/1.1 and refuses a gRPC call with HTTP_1_1_REQUIRED.
internal static class ModularMonolithProcess
{
    public static HostProcess Start(string connectionString)
    {
        Dictionary<string, string> environment = new(TestIssuer.HostEnvironment)
        {
            ["ConnectionStrings__app"] = connectionString,
            ["PouryaKit__TrustedPeers__Peers__0"] = AppFixture.TrustedPeer,
            ["Kestrel__EndpointDefaults__Protocols"] = "Http2",
            ["Logging__Console__FormatterName"] = "json",
            ["Logging__Console__FormatterOptions__IncludeScopes"] = "true",
        };
        return HostProcess.Start("ModularMonolith.Host", environment);
    }
}
