namespace PouryaKit.Domain.Tests;

public class AggregateRootTests
{
    [Fact]
    public void Raising_domain_events_records_them_in_order_and_does_not_move_the_version()
    {
        var product = Product.Create(TypedId.New<ProductId>(), "Lamp").Value;

        product.Rename("Desk lamp");

        Assert.Collection(
            product.DomainEvents,
            first => Assert.Equal("Lamp", Assert.IsType<ProductCreated>(first).Name),
            second => Assert.Equal("Desk lamp", Assert.IsType<ProductRenamed>(second).NewName)
        );
        Assert.Equal(0, product.Version);
    }

    [Fact]
    public void The_unit_of_work_contract_clears_the_recorded_domain_events()
    {
        IAggregateRoot product = Product.Create(TypedId.New<ProductId>(), "Lamp").Value;

        product.ClearDomainEvents();

        Assert.Empty(product.DomainEvents);
    }

    [Fact]
    public void Entities_are_equal_only_when_type_and_id_are_equal()
    {
        var id = TypedId.New<ProductId>();
        var product = Product.Create(id, "Lamp").Value;

        Assert.Equal(product, Product.Create(id, "Desk lamp").Value);
        Assert.NotEqual(product, Product.Create(TypedId.New<ProductId>(), "Lamp").Value);
        Assert.NotEqual<object>(product, new DiscontinuedProduct(id));
    }
}
