using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ModularMonolith.Tests.Stub;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Email;
using PouryaKit.Email.MailKit;
using PouryaKit.Messaging;
using PouryaKit.Modularity;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

// Ticket #124: a Notification Handler sends email through the Host's one IEmailSender, MailKit's, configured from
// PouryaKit:Email and validated at start, and the mail arrives at a real SMTP server. G.2's guarantee holds at this
// seam: a rolled-back Operation creates no Notification and so sends no mail. Its stated limit too: a mail sent by a
// delivery that then fails is sent again by the retry.
public sealed class EmailTests(AppFixture app, MailFixture mail) : IClassFixture<MailFixture>
{
    private const string Recipient = "ada@example.com";

    private IGatewayProcessor<StubModule> Stub => mail.Host.Services.GetRequiredService<IGatewayProcessor<StubModule>>();

    [Fact]
    public async Task A_notification_handler_sends_a_mail_that_arrives_once_its_operation_commits()
    {
        var text = Unique();

        var written = await Stub.SendAsync(
            new WriteNoteRaisingCommand(text, [new NoteMailedDomainEvent(Recipient, text)]),
            TestContext.Current.CancellationToken
        );

        Assert.True(written.IsSuccess);
        var arrived = Assert.Single(await mail.WaitForMailsAsync(text, 1));
        Assert.Equal(
            (Recipient, "shop@example.com", "Modular Monolith", $"The note {text} is written."),
            (Assert.Single(arrived.To).Address, Assert.Single(arrived.From).Address, arrived.From[0].Name, arrived.TextBody?.TrimEnd())
        );
    }

    [Fact]
    public async Task A_rolled_back_operation_sends_no_mail()
    {
        var rolledBack = Unique();
        var committed = Unique();

        var refused = await Stub.SendAsync(
            new WriteNoteRaisingCommand(rolledBack, [new NoteMailedDomainEvent(Recipient, rolledBack)], Fail: true),
            TestContext.Current.CancellationToken
        );
        await Stub.SendAsync(
            new WriteNoteRaisingCommand(committed, [new NoteMailedDomainEvent(Recipient, committed)]),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(WriteNoteRaisingCommandHandler.Refused, Assert.Single(refused.Errors));
        // The committed Operation's mail, sent after the rolled-back one returned, arrives; the other never existed.
        await mail.WaitForMailsAsync(committed, 1);
        Assert.Empty(await mail.MailsAsync(rolledBack));
    }

    [Fact]
    public async Task A_mail_whose_delivery_fails_after_sending_it_is_sent_again_by_the_retry()
    {
        var text = Unique();
        var mailed = new NoteMailedThenFailingDomainEvent(Recipient, text);
        var subscription = MessageNames.SubscriptionNameOf(typeof(MailNoteThenFailOnceNotificationHandler), typeof(NoteMailedThenFailingDomainEvent));

        await Stub.SendAsync(new WriteNoteRaisingCommand(text, [mailed]), TestContext.Current.CancellationToken);

        // The retry succeeded: its Receipt is committed, so the delivery is over and no further attempt sends again.
        await mail.WaitUntilAsync(
            $"exists (select from stub.pouryakit_receipts where subscription = '{subscription}' and message_id = '{mailed.Id}')",
            "The retried delivery committed no Receipt."
        );
        Assert.Equal(2, (await mail.MailsAsync(text)).Count);
    }

    [Fact]
    public async Task A_recipient_that_is_not_a_mailbox_address_fails_the_send_with_its_error_and_sends_nothing()
    {
        var text = Unique();

        var sent = await mail.Host.Services.GetRequiredService<IEmailSender>()
            .SendAsync(new EmailMessage("ada", text, "Never sent."), TestContext.Current.CancellationToken);

        Assert.Equal(EmailErrors.InvalidAddress, Assert.Single(sent.Errors));
        Assert.Empty(await mail.MailsAsync(text));
    }

    [Fact]
    public async Task A_host_whose_email_security_is_not_a_secure_socket_option_fails_to_start_naming_the_option()
    {
        // The binder reads a number into the enum; MailKit would connect in plain SMTP for an undefined value.
        await using var host = AppFixture.ComposeHost("Development", app.ConnectionString, new RecordedLogs(), null, "--PouryaKit:Email:Security=99");

        var thrown = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            "PouryaKitMailKitOptions.Security is not a SecureSocketOptions value; set None, Auto, SslOnConnect, StartTls or StartTlsWhenAvailable.",
            Assert.Single(thrown.Failures)
        );
    }

    [Fact]
    public async Task A_host_whose_email_options_name_no_smtp_server_fails_to_start_naming_the_option()
    {
        await using var host = AppFixture.ComposeHost("Development", app.ConnectionString, new RecordedLogs(), null, "--PouryaKit:Email:Host=");

        var thrown = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            "PouryaKitMailKitOptions.Host names no SMTP server; set the host name of the server the Host sends its email through.",
            Assert.Single(thrown.Failures)
        );
    }

    [Theory]
    [InlineData("a type, before")]
    [InlineData("a type, after")]
    [InlineData("a factory, after")]
    public void A_host_that_registers_another_email_sender_fails_when_it_is_built_naming_both(string other)
    {
        const string MailKit = "the factory PouryaKit.Email.MailKit.MailKitServiceCollectionExtensions.Create";
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.AddPouryaKitCompositionChecks();
        if (other == "a type, before")
        {
            builder.Services.AddSingleton<IEmailSender, LocalEmailSender>();
        }

        builder.Services.AddPouryaKitMailKit();
        string[] registered = other switch
        {
            "a type, before" => [typeof(LocalEmailSender).FullName!, MailKit],
            "a type, after" => [MailKit, typeof(LocalEmailSender).FullName!],
            _ => [MailKit, $"the factory {typeof(EmailTests).FullName}.{nameof(LocalFactory)}"],
        };
        switch (other)
        {
            case "a type, after":
                builder.Services.AddSingleton<IEmailSender, LocalEmailSender>();
                break;
            case "a factory, after":
                builder.Services.AddSingleton<IEmailSender>(LocalFactory);
                break;
        }

        var thrown = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Equal(
            $"The Host registers 2 IEmailSender providers, {string.Join(", ", registered)}, and composes exactly one: remove all but one of its email sender registrations.",
            thrown.Message
        );
    }

    [Fact]
    public void A_host_that_removes_the_email_sender_fails_when_it_is_built_naming_the_contract()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.AddPouryaKitCompositionChecks();
        builder.Services.AddPouryaKitMailKit();
        builder.Services.RemoveAll<IEmailSender>();

        var thrown = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Equal(
            "The Host registers no IEmailSender provider, and composes exactly one: AddPouryaKitMailKit registered one, then it was removed.",
            thrown.Message
        );
    }

    private static string Unique() => $"mail {Guid.CreateVersion7():N}";

    // A factory typed as the contract, as a Host's own registration often is.
    private static IEmailSender LocalFactory(IServiceProvider provider) => new LocalEmailSender();

    // Another sender a Host could register, never called.
    private sealed class LocalEmailSender : IEmailSender
    {
        public Task<Result> SendAsync(EmailMessage message, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
