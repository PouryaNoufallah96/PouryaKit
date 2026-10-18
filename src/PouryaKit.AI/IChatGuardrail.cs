using Microsoft.Extensions.AI;

namespace PouryaKit.AI;

/// <summary>
/// The application's check of each call the Host's chat client makes to the model, in every run and every agent of a
/// workflow: registered as a singleton, it is called before the call and after it, and the Kit has no policy of its own.
/// </summary>
public interface IChatGuardrail
{
    /// <summary>
    /// Checks the messages about to be sent to the model, the call's instructions first as the system message the model
    /// is given, such as an agent's and its context providers': null lets the call go, and a refusal is the answer the run
    /// gets instead, as a response the model's content filter ended, without the model being called.
    /// </summary>
    Task<string?> CheckRequestAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken);

    /// <summary>
    /// Checks the model's response before any tool call it asks for runs: null lets it through, and a refusal replaces it,
    /// as a response the model's content filter ended, so no tool it asked for runs.
    /// </summary>
    Task<string?> CheckResponseAsync(ChatResponse response, CancellationToken cancellationToken);
}
