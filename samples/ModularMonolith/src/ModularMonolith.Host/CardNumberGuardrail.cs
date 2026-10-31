using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using PouryaKit.AI;

namespace ModularMonolith.Host;

/// <summary>
/// The application's policy for every call the Host's chat client makes to the model, Catalog's agent's included: no card
/// number, sixteen digits in groups of four or not, is sent to the model's provider or answered back, in a message's text,
/// a tool call's arguments or what a tool answered, such as a product named with one. A product's id, a GUID, holds none:
/// its groups are 8-4-4-4-12, and a match is bounded by neither a word character nor a hyphen.
/// </summary>
public sealed partial class CardNumberGuardrail : IChatGuardrail
{
    public const string Refusal = "Card numbers are not taken here: ask again without one.";

    public Task<string?> CheckRequestAsync(
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken
    ) => Task.FromResult(HoldsCardNumber(messages) ? Refusal : null);

    public Task<string?> CheckResponseAsync(
        ChatResponse response,
        CancellationToken cancellationToken
    ) => Task.FromResult(HoldsCardNumber(response.Messages) ? Refusal : null);

    private static bool HoldsCardNumber(IEnumerable<ChatMessage> messages) =>
        messages
            .SelectMany(message => message.Contents)
            .Any(content => CardNumber().IsMatch(TextOf(content)));

    // A message's Text is its TextContent alone: a tool call's arguments and a tool's answer are read as their JSON.
    private static string TextOf(AIContent content) =>
        content switch
        {
            TextContent text => text.Text,
            FunctionCallContent call => JsonSerializer.Serialize(call.Arguments),
            FunctionResultContent result => JsonSerializer.Serialize(result.Result),
            _ => "",
        };

    [GeneratedRegex(@"(?<![\w-])\d{4}(?:[ -]?\d{4}){3}(?![\w-])")]
    private static partial Regex CardNumber();
}
