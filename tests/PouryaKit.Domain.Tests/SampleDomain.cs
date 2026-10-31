namespace PouryaKit.Domain.Tests;

// A small Business Area written the way a feature author would, so tests use only the public API.
public sealed record ProductId(Guid Value) : TypedId<Guid>(Value);

public sealed record OrderId(Guid Value) : TypedId<Guid>(Value);

public sealed record InvoiceNumber(long Value) : TypedId<long>(Value);

public sealed record LineNumber(int Value) : TypedId<int>(Value);

internal sealed record Sku(string Value) : TypedId<string>(Value);

public sealed record PriceId(decimal Value) : TypedId<decimal>(Value);

public sealed record ProductCreatedDomainEvent(ProductId ProductId, string Name) : DomainEvent;

public sealed record ProductRenamedDomainEvent(ProductId ProductId, string NewName) : DomainEvent;

public static class ProductErrors
{
    public static readonly Error NameRequired = Error.Validation(
        "Product.NameRequired",
        "A product needs a name."
    );
}

public sealed class ProductNameMustNotBeBlank(string name) : IBusinessRule
{
    public Error Error => ProductErrors.NameRequired;

    public bool IsBroken() => string.IsNullOrWhiteSpace(name);
}

public sealed class Product : AggregateRoot<ProductId>
{
    public string Name { get; private set; } = "";

    public static Result<Product> Create(ProductId id, string name)
    {
        var rule = CheckRule(new ProductNameMustNotBeBlank(name));
        if (rule.IsFailure)
        {
            return rule.Errors;
        }

        var product = new Product { Id = id, Name = name };
        product.Raise(new ProductCreatedDomainEvent(id, name));
        return product;
    }

    public void Rename(string newName)
    {
        Name = newName;
        Raise(new ProductRenamedDomainEvent(Id, newName));
    }
}

public sealed class DiscontinuedProduct : AggregateRoot<ProductId>
{
    public DiscontinuedProduct(ProductId id) => Id = id;
}
