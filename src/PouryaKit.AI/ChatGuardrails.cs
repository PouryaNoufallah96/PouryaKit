using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace PouryaKit.AI;

// Runs the application's guardrails around each call to the model, inside function invocation, so a refused response's
// tool calls never reach it. The first refusal wins and ends the call as the provider's content filter would. A streamed
// call is checked whole: its updates reach the caller once the response completed and passed.
internal sealed class ChatGuardrails(IChatClient inner, IChatGuardrail[] guardrails) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        IReadOnlyList<ChatMessage> request = [.. messages];
        foreach (var guardrail in guardrails)
        {
            if (await guardrail.CheckRequestAsync(request, cancellationToken) is { } refusal)
            {
                return Refused(refusal);
            }
        }

        var response = await base.GetResponseAsync(request, options, cancellationToken);
        foreach (var guardrail in guardrails)
        {
            if (await guardrail.CheckResponseAsync(response, cancellationToken) is { } refusal)
            {
                return Refused(refusal);
            }
        }

        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        foreach (var update in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    private static ChatResponse Refused(string refusal) =>
        new(new ChatMessage(ChatRole.Assistant, refusal)) { FinishReason = ChatFinishReason.ContentFilter };
}
