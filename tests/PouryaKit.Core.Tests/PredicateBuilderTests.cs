using System.Linq.Expressions;

namespace PouryaKit.Core.Tests;

public class PredicateBuilderTests
{
    // The right predicate counts its evaluations, so an eager (bitwise) composition is seen to run it when the left
    // predicate has already decided the result. Both predicates read the parameter, so the composed lambda only compiles
    // when each predicate's parameter was replaced by the composed lambda's parameter.
    private int rightEvaluations;

    [Theory]
    [InlineData(3, false, false)]
    [InlineData(4, false, true)]
    [InlineData(13, true, false)]
    [InlineData(12, true, true)]
    public void AndAlso_is_true_when_both_are_and_evaluates_the_right_predicate_only_when_the_left_is_true(int value, bool left, bool right)
    {
        var composed = AboveTen().AndAlso(Even()).Compile();

        Assert.Equal(left && right, composed(value));
        Assert.Equal(left ? 1 : 0, rightEvaluations);
    }

    [Theory]
    [InlineData(3, false, false)]
    [InlineData(4, false, true)]
    [InlineData(13, true, false)]
    [InlineData(12, true, true)]
    public void OrElse_is_true_when_either_is_and_evaluates_the_right_predicate_only_when_the_left_is_false(int value, bool left, bool right)
    {
        var composed = AboveTen().OrElse(Even()).Compile();

        Assert.Equal(left || right, composed(value));
        Assert.Equal(left ? 0 : 1, rightEvaluations);
    }

    [Fact]
    public void A_lambda_inside_the_right_predicate_that_binds_the_left_predicates_parameter_keeps_its_own_binding()
    {
        // q => new[] { 10 }.Any(p => p > q), where the inner lambda declares the very ParameterExpression the left
        // predicate p => p > 0 takes. Putting the right body on the left parameter would turn p > q into p > p.
        var p = Expression.Parameter(typeof(int), "p");
        var q = Expression.Parameter(typeof(int), "q");
        var left = Expression.Lambda<Func<int, bool>>(Expression.GreaterThan(p, Expression.Constant(0)), p);
        var anyAboveQ = Expression.Call(
            typeof(Enumerable),
            nameof(Enumerable.Any),
            [typeof(int)],
            Expression.Constant(new[] { 10 }),
            Expression.Lambda<Func<int, bool>>(Expression.GreaterThan(p, q), p)
        );
        var right = Expression.Lambda<Func<int, bool>>(anyAboveQ, q);

        Assert.True(left.AndAlso(right).Compile()(5));
        Assert.True(Expression.Lambda<Func<int, bool>>(Expression.LessThan(p, Expression.Constant(0)), p).OrElse(right).Compile()(5));
    }

    private static Expression<Func<int, bool>> AboveTen() => number => number > 10;

    private Expression<Func<int, bool>> Even() => other => Counted(other % 2 == 0);

    private bool Counted(bool result)
    {
        rightEvaluations++;
        return result;
    }
}
