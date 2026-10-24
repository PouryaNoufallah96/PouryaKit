using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using PouryaKit.AI;

namespace ModularMonolith.Host;

/// <summary>
/// The application's policy for every call the Host's chat client makes to the model, Catalog's agent's included: no card
/// number, sixteen digits in groups of four or not, is sent to the model's provider or answered back. A product's id, a
/// GUID, holds none, since its groups are hyphenated.
/// </summary>
public sealed partial class CardNumberGuardrail : IChatGuardrail
{
    public const string Refusal = "Card numbers are not taken here: ask again without one.";

    public Task<string?> CheckRequestAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken) =>
        Task.FromResult(messages.Any(message => CardNumber().IsMatch(message.Text)) ? Refusal : null);

    public Task<string?> CheckResponseAsync(ChatResponse response, CancellationToken cancellationToken) =>
        Task.FromResult(CardNumber().IsMatch(response.Text) ? Refusal : null);

    [GeneratedRegex(@"(?<![\w-])\d{4}(?:[ -]?\d{4}){3}(?![\w-])")]
    private static partial Regex CardNumber();
}
