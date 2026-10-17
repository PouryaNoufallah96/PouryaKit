namespace ModularMonolith.AppHost;

/// <summary>The AppHost's own options, which its tests run it with.</summary>
public static class AppHostOptions
{
    /// <summary>
    /// Headless: no dashboard, a tracing and logging UI the Kit does not use. A developer who wants it locally sets
    /// <c>DisableDashboard</c> to false in their copy.
    /// </summary>
    public static DistributedApplicationOptions Create(string[] args) => new() { Args = args, DisableDashboard = true };
}
