using System.Collections.Immutable;
using System.Reflection;

namespace PouryaKit.Domain.Tests;

public class ResultTests
{
    private static readonly Error Missing = Error.NotFound("Product.NotFound", "No such product.");
    private static readonly Error Taken = Error.Conflict("Product.NameTaken", "The name is taken.");

    [Fact]
    public void A_value_converts_to_a_success_holding_it()
    {
        Result<int> result = 42;

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Errors);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void An_error_converts_to_a_failure_whose_value_cannot_be_read()
    {
        Result<int> result = Missing;

        Assert.True(result.IsFailure);
        Assert.Equal([Missing], result.Errors);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void An_error_converts_to_a_failed_result_without_a_value_type()
    {
        Result result = Missing;

        Assert.True(result.IsFailure);
        Assert.Equal([Missing], result.Errors);
        Assert.True(Result.Success().IsSuccess);
    }

    [Fact]
    public void Null_never_converts_to_success()
    {
        string? nothing = null;

        Assert.Throws<ArgumentNullException>(() =>
        {
            Result<string> result = nothing!;
            return result;
        });
    }

    [Fact]
    public void A_failure_passes_every_error_in_order_to_a_result_of_another_type()
    {
        Result<string> inner = ImmutableArray.Create(Missing, Taken);

        Result<int> outer = inner.Errors;

        Assert.True(outer.IsFailure);
        Assert.Equal([Missing, Taken], outer.Errors);
    }

    [Fact]
    public void A_failure_needs_at_least_one_error()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            Result<int> result = ImmutableArray<Error>.Empty;
            return result;
        });
        Assert.Throws<ArgumentException>(() =>
        {
            Result result = default(ImmutableArray<Error>);
            return result;
        });
    }

    [Fact]
    public void No_operator_converts_a_result_to_its_value()
    {
        var conversions = typeof(Result<int>)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name is "op_Implicit" or "op_Explicit");

        Assert.All(conversions, method => Assert.Equal(typeof(Result<int>), method.ReturnType));
    }
}
