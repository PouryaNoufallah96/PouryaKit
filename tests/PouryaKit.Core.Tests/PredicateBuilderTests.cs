using System.Linq.Expressions;

namespace PouryaKit.Core.Tests;

public class PredicateBuilderTests
{
    // The right predicate counts its evaluations, so an eager (bitwise) composition is seen to run it when the left
    // predicate has already decided the result. Both predicates read the parameter, so the composed lambda only compiles
    // when the right predicate's parameter was replaced by the left one's.
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

    private static Expression<Func<int, bool>> AboveTen() => number => number > 10;

    private Expression<Func<int, bool>> Even() => other => Counted(other % 2 == 0);

    private bool Counted(bool result)
    {
        rightEvaluations++;
        return result;
    }
}
