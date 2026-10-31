using PouryaKit.Domain;
using PouryaKit.Email;

// The packed Email contract, implemented as a provider package would: a send returns a Result, and a recipient that is
// not an address fails with the contract's Error.
IEmailSender sender = new RecordingSender();
var sent = await sender.SendAsync(
    new EmailMessage("ada@example.com", "Welcome", "Hello Ada."),
    CancellationToken.None
);
var refused = await sender.SendAsync(
    new EmailMessage("ada", "Welcome", "Hello Ada."),
    CancellationToken.None
);
Console.WriteLine(sent.IsSuccess && refused.Errors.Single() == EmailErrors.InvalidAddress ? 1 : 0);

internal sealed class RecordingSender : IEmailSender
{
    public Task<Result> SendAsync(EmailMessage message, CancellationToken cancellationToken) =>
        Task.FromResult(message.To.Contains('@') ? Result.Success() : EmailErrors.InvalidAddress);
}
