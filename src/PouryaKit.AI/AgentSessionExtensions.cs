using Microsoft.Agents.AI;

namespace PouryaKit.AI;

/// <summary>Keeps an agent's sessions in the application's <see cref="IAgentSessionStore"/>, across scopes and Hosts.</summary>
public static class AgentSessionExtensions
{
    extension(AIAgent agent)
    {
        /// <summary>
        /// The session <paramref name="store"/> holds under <paramref name="key"/>, restored by this agent with the history
        /// it had when it was saved, or a new session when the store holds none.
        /// </summary>
        public async Task<AgentSession> LoadSessionAsync(IAgentSessionStore store, string key, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(agent);
            ArgumentNullException.ThrowIfNull(store);
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            return await store.FindAsync(key, cancellationToken) is { } saved
                ? await agent.DeserializeSessionAsync(saved, cancellationToken: cancellationToken)
                : await agent.CreateSessionAsync(cancellationToken);
        }

        /// <summary>
        /// Saves <paramref name="session"/>, as this agent serializes it with its history, in <paramref name="store"/> under
        /// <paramref name="key"/>; the Kit keeps no copy, so a session not saved is lost with its scope.
        /// </summary>
        public async Task SaveSessionAsync(IAgentSessionStore store, string key, AgentSession session, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(agent);
            ArgumentNullException.ThrowIfNull(store);
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentNullException.ThrowIfNull(session);
            await store.SaveAsync(key, await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken), cancellationToken);
        }
    }
}
