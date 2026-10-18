using Microsoft.Extensions.AI;

namespace PouryaKit.AI;

// Withholds from each call every tool its options give that the allow-list does not name, before function invocation
// reads them: the model is not offered the tool, and a call it asks for anyway is answered as a function that does not
// exist, so the tool never runs. A declaration that is no function, such as a handoff workflow's handoff_to_ tools, runs
// nothing here: function invocation hands its call back to the caller, so it passes unnamed.
internal sealed class ToolAllowList(IChatClient inner, ISet<string> allowed) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    ) => base.GetResponseAsync(messages, Allowed(options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    ) => base.GetStreamingResponseAsync(messages, Allowed(options), cancellationToken);

    private ChatOptions? Allowed(ChatOptions? options)
    {
        if (options?.Tools is not { } tools || tools.All(IsAllowed))
        {
            return options;
        }

        var only = options.Clone();
        only.Tools = [.. tools.Where(IsAllowed)];
        return only;
    }

    private bool IsAllowed(AITool tool) => allowed.Contains(tool.Name) || tool is AIFunctionDeclaration and not AIFunction;
}
