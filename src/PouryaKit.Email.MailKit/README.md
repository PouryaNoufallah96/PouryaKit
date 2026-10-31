![PouryaKit.Email.MailKit](https://raw.githubusercontent.com/PouryaNoufallah96/pouryakit-pub/assets-v1/assets/covers/PouryaKit.Email.MailKit.png)

[![NuGet](https://img.shields.io/nuget/vpre/PouryaKit.Email.MailKit?style=flat-square&logo=nuget&color=615fff)](https://www.nuget.org/packages/PouryaKit.Email.MailKit)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)
![License: MIT](https://img.shields.io/badge/license-MIT-615fff?style=flat-square)

# PouryaKit.Email.MailKit

The Host's SMTP sender on MailKit. It supplies `IEmailSender` of [`PouryaKit.Email`](https://www.nuget.org/packages/PouryaKit.Email) from options the Host checks before it serves, so a Module references only the contract and never names MailKit.

## Install

```bash
dotnet add package PouryaKit.Email.MailKit --prerelease
```

## When to use it

- A Host sends the mail its Modules ask for.
- You configure the Host's SMTP server and sender address.
- You test mail against a Papercut container.

It implements [`PouryaKit.Email`](https://www.nuget.org/packages/PouryaKit.Email). A Host composes exactly one sender.

## Quick start

Register it in the Host and bind its options from configuration, as the Proving Sample does:

```csharp
builder.Services.AddPouryaKitMailKit();
builder.Services.Configure<PouryaKitMailKitOptions>(
    builder.Configuration.GetSection("PouryaKit:Email")
);
```

## What you get

- The contract of `IEmailSender` on MailKit.
- Options checked before the Host serves, not at the first send.
- A Host that fails when it is built unless it composes exactly one sender.

> [!TIP]
> The server's acceptance is not guaranteed, and a send that succeeded may repeat: [what the Kit guarantees](https://pouryakit.vercel.app/docs/capabilities/email#what-the-kit-guarantees).

## Learn more

- [Registering](https://pouryakit.vercel.app/docs/capabilities/email#registering)
- [Email](https://pouryakit.vercel.app/docs/capabilities/email)
- [Testing](https://pouryakit.vercel.app/docs/capabilities/email#testing)
