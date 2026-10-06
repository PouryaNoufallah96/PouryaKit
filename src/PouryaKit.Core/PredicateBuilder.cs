using System.Linq.Expressions;

namespace PouryaKit.Core;

/// <summary>
/// Composes two predicates over the same type into one, for a LINQ provider such as EF Core or for compiling.
/// </summary>
/// <remarks>
/// Input contract: both predicates are non-null lambdas of one parameter each. The composed predicate takes the left
/// predicate's parameter, and the right predicate's parameter is replaced by it, so a provider sees one lambda and no
/// invocation. The two bodies are combined with <see cref="Expression.AndAlso(Expression, Expression)"/> or
/// <see cref="Expression.OrElse(Expression, Expression)"/>, never the bitwise <c>And</c> or <c>Or</c>, so the right
/// predicate runs only when the left one has not already decided the result.
/// </remarks>
public static class PredicateBuilder
{
    extension<T>(Expression<Func<T, bool>> left)
    {
        /// <summary>
        /// True when both predicates are; <paramref name="right"/> is evaluated only when the left predicate is true.
        /// </summary>
        public Expression<Func<T, bool>> AndAlso(Expression<Func<T, bool>> right) => Compose(left, right, Expression.AndAlso);

        /// <summary>
        /// True when either predicate is; <paramref name="right"/> is evaluated only when the left predicate is false.
        /// </summary>
        public Expression<Func<T, bool>> OrElse(Expression<Func<T, bool>> right) => Compose(left, right, Expression.OrElse);
    }

    private static Expression<Func<T, bool>> Compose<T>(
        Expression<Func<T, bool>> left,
        Expression<Func<T, bool>> right,
        Func<Expression, Expression, BinaryExpression> combine
    )
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var parameter = left.Parameters[0];
        var rightBody = new ParameterReplacer(right.Parameters[0], parameter).Visit(right.Body);
        return Expression.Lambda<Func<T, bool>>(combine(left.Body, rightBody), parameter);
    }

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}
