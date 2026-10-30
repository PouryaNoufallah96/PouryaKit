![PouryaKit.Email](https://raw.githubusercontent.com/PouryaNoufallah96/pouryakit-public/assets-v1/assets/covers/PouryaKit.Email.png)

[![NuGet](https://img.shields.io/nuget/vpre/PouryaKit.Email?style=flat-square&logo=nuget&color=615fff)](https://www.nuget.org/packages/PouryaKit.Email)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)
![License: MIT](https://img.shields.io/badge/license-MIT-615fff?style=flat-square)

# PouryaKit.Email

One contract that sends a plain-text mail and returns a Result. `IEmailSender` sends one mail to one recipient, and a Module references only this contract and never names the provider. Mail goes out from a Notification Handler, after the Operation that raised the Domain Event committed, so a rolled-back Operation sends no mail.

## Install

```bash
dotnet add package PouryaKit.Email --prerelease
```

## When to use it

- An Operation's outcome is mailed to someone: a welcome, a receipt, a notice.
- You need to know what a rolled-back Operation or a failed delivery does to a mail.

[`PouryaKit.Email.MailKit`](https://www.nuget.org/packages/PouryaKit.Email.MailKit) is the Host's provider, and [`PouryaKit.Messaging`](https://www.nuget.org/packages/PouryaKit.Messaging) holds the Notification Handler it sends from.

## Quick start

Send from a Notification Handler, as the Kit's tests do:

```csharp
internal sealed record NoteMailedDomainEvent(string To, string Text) : DomainEvent;

internal sealed class MailNoteNotificationHandler(IEmailSender email) : INotificationHandler<NoteMailedDomainEvent>
{
    public Task<Result> Handle(NoteMailedDomainEvent domainEvent, CancellationToken cancellationToken) =>
        email.SendAsync(new EmailMessage(domainEvent.To, domainEvent.Text, $"The note {domainEvent.Text} is written."), cancellationToken);
}
```

## What you get

- A `Result` from each send, which the Notification Handler returns. A server that cannot be reached, refuses the login or refuses the mail throws, and the delivery is retried.
- No mail for a rolled-back Operation.
- A committed Operation's mail attempted by a durable delivery, retried and in the end set aside as a dead letter with its reason.

> [!WARNING]
> A mail may be sent twice. Mail is not transactional: when the delivery that sent a mail fails afterwards, its retry sends the mail again. Send last in the handler, and give each mail its own Notification Handler.

## Learn more

- [Email](https://pouryakit.vercel.app/docs/capabilities/email)
- [Sending from a Notification Handler](https://pouryakit.vercel.app/docs/capabilities/email#sending-from-a-notification-handler)
- [What the Kit guarantees](https://pouryakit.vercel.app/docs/capabilities/email#what-the-kit-guarantees)
- [Outbox and Notifications](https://pouryakit.vercel.app/docs/concepts/outbox-and-notifications)
