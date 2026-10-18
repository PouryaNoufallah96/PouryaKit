using System.Text.Json;

namespace PouryaKit.AI;

/// <summary>
/// The application's store of agent sessions, which the Kit never implements: it keeps each session's state, as the
/// agent serialized it, under the key the application gives the session, such as a conversation's id.
/// </summary>
public interface IAgentSessionStore
{
    /// <summary>The session state saved under <paramref name="key"/>, or null when none is.</summary>
    Task<JsonElement?> FindAsync(string key, CancellationToken cancellationToken);

    /// <summary>Saves <paramref name="session"/>, a session's state, under <paramref name="key"/>, replacing what was saved there.</summary>
    Task SaveAsync(string key, JsonElement session, CancellationToken cancellationToken);
}
