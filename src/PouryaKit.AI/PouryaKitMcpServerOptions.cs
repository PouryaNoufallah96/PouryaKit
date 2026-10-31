namespace PouryaKit.AI;

/// <summary>
/// One MCP server of <see cref="PouryaKitAIOptions.McpServers"/>: a <see cref="Command"/> the Host starts and speaks to over
/// its standard input and output, or an <see cref="Endpoint"/> it reaches over HTTP, never both.
/// </summary>
public sealed class PouryaKitMcpServerOptions
{
    /// <summary>The program that starts the server, for a server over stdio; null for one over HTTP.</summary>
    public string? Command { get; set; }

    /// <summary>The arguments <see cref="Command"/> is started with.</summary>
    public IList<string> Arguments { get; } = [];

    /// <summary>The directory <see cref="Command"/> starts in; by default the Host's.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// The variables of the server's environment beside the few a process needs to start, such as <c>PATH</c> and
    /// <c>HOME</c>: the server inherits none of the Host's others, so no secret of the Host reaches it unnamed.
    /// </summary>
    public IDictionary<string, string> Environment { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The server's MCP endpoint, an absolute HTTP or HTTPS address, for a server over HTTP; null for one over stdio.</summary>
    public Uri? Endpoint { get; set; }
}
