namespace PouryaKit.Domain.Tests;

public class ErrorTests
{
    public static TheoryData<Func<string, string, Error>, ErrorKind> Factories =>
        new()
        {
            { Error.Validation, ErrorKind.Validation },
            { Error.NotFound, ErrorKind.NotFound },
            { Error.Conflict, ErrorKind.Conflict },
            { Error.Forbidden, ErrorKind.Forbidden },
            { Error.Unexpected, ErrorKind.Unexpected },
        };

    [Theory]
    [MemberData(nameof(Factories))]
    public void Each_error_carries_its_semantic_kind(Func<string, string, Error> create, ErrorKind kind)
    {
        var error = create("Product.Code", "A message.");

        Assert.Equal(kind, error.Kind);
        Assert.Equal("Product.Code", error.Code);
        Assert.Equal("A message.", error.Message);
    }

    [Fact]
    public void A_broken_business_rule_returns_its_error_and_a_kept_one_returns_success()
    {
        var broken = Product.Create(TypedId.New<ProductId>(), " ");
        var kept = Product.Create(TypedId.New<ProductId>(), "Lamp");

        Assert.Equal([ProductErrors.NameRequired], broken.Errors);
        Assert.True(kept.IsSuccess);
    }
}
