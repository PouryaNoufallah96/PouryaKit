![PouryaKit.Validation](https://raw.githubusercontent.com/PouryaNoufallah96/pouryakit-public/assets-v1/assets/covers/PouryaKit.Validation.png)

[![NuGet](https://img.shields.io/nuget/vpre/PouryaKit.Validation?style=flat-square&logo=nuget&color=615fff)](https://www.nuget.org/packages/PouryaKit.Validation)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)
![License: MIT](https://img.shields.io/badge/license-MIT-615fff?style=flat-square)

# PouryaKit.Validation

FluentValidation validators for commands and queries, run as one step of the Kit's fixed pipeline. Each validator sits beside its Feature, and every failure of every validator comes back as one failed Result, its Errors naming the input they are about, before the handler runs.

## Install

```bash
dotnet add package PouryaKit.Validation --prerelease
```

## When to use it

- A command's or query's input needs checking before its handler runs.
- A paged query must refuse a size, sort or filter it did not declare.

It plugs into [`PouryaKit.Cqrs`](https://www.nuget.org/packages/PouryaKit.Cqrs), whose pipeline runs it, and [`PouryaKit.Web`](https://www.nuget.org/packages/PouryaKit.Web) answers its Errors in a problem response, each naming its input.

## Quick start

Register the Module's validators in its Composition Root:

```csharp
module.AddPouryaKitValidation();
```

Write one validator per command or query that needs input checks, beside it in the Feature, as the Proving Sample does:

```csharp
internal sealed class CreateProductCommandValidator : AbstractValidator<CreateProductCommand>
{
    public CreateProductCommandValidator()
    {
        RuleFor(command => command.ProductId).NotEmpty();
        RuleFor(command => command.CategoryId).NotEmpty();
        RuleFor(command => command.Name).NotEmpty().MaximumLength(200);
    }
}
```

## What you get

- One validation step, in its fixed place in the pipeline, so a handler sees only input that passed.
- Every failure at once, as Errors in the Result; nothing throws.
- Validators found in the Module's own assembly, internal ones included.
- Rules for a Page's size, sort and filter.

> [!TIP]
> Validate the input's shape here. A business rule that depends on state belongs in the aggregate, where it returns an Error.

## Learn more

- [Validate a command](https://pouryakit.vercel.app/docs/guides/validate)
- [Validation](https://pouryakit.vercel.app/docs/capabilities/commands-queries-validation#validation)
- [Commands, queries and validation](https://pouryakit.vercel.app/docs/capabilities/commands-queries-validation)
