using PouryaKit.Domain;

namespace Ledger.Accounts.Errors;

internal static class AccountErrors
{
    public static readonly Error NotFound = Error.NotFound("Account.NotFound", "The account does not exist.");
}
