using MailKit.Security;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Email;
using PouryaKit.Email.MailKit;

// The packed MailKit sender, registered and configured as a Host would: the Host's one IEmailSender, a singleton, over
// options it binds and the Kit validates when the Host starts.
var services = new ServiceCollection().AddPouryaKitMailKit();
services.Configure<PouryaKitMailKitOptions>(mail =>
{
    mail.Host = "smtp.example.com";
    mail.From = "Shop <shop@example.com>";
});
var registered = services.Any(descriptor =>
    descriptor.ServiceType == typeof(IEmailSender)
    && descriptor.Lifetime == ServiceLifetime.Singleton
);
var defaults = new PouryaKitMailKitOptions();
Console.WriteLine(
    registered && defaults is { Port: 587, Security: SecureSocketOptions.StartTls } ? 1 : 0
);
