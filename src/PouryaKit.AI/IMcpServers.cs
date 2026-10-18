using ModelContextProtocol.Client;

namespace PouryaKit.AI;

/// <summary>
/// The Host's MCP servers, those <see cref="PouryaKitAIOptions.McpServers"/> names, whose clients the Host owns: each is
/// connected at its first listing and stays connected until the Host's services are disposed, whatever runs its tools.
/// </summary>
public interface IMcpServers
{
    /// <summary>
    /// The tools of the server named <paramref name="server"/>, which a run is given beside its Gateway tools: each calls
    /// that server when the model asks for it, under the run's allow-list and iteration cap.
    /// </summary>
    Task<IReadOnlyList<McpClientTool>> ListToolsAsync(string server, CancellationToken cancellationToken = default);
}
