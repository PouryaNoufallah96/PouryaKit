using PouryaKit.Domain;

namespace Billing.Quotes.Errors;

internal static class QuoteErrors
{
    public static readonly Error NotFound = Error.NotFound("Quote.NotFound", "The quote does not exist.");

    public static readonly Error NotOpen = Error.Conflict("Quote.NotOpen", "The quote is no longer open.");
}
