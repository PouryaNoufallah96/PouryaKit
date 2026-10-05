using System.Collections.Immutable;

namespace PouryaKit.Domain;

/// <summary>
/// The outcome of an Operation or business rule: success, or one or more Errors. A value or an
/// <see cref="Error"/> converts to a Result implicitly; nothing converts a Result back to its value.
/// </summary>
public class Result
{
    private static readonly Result SuccessResult = new([]);

    private protected Result(ImmutableArray<Error> errors) => Errors = errors;

    /// <summary>Every Error of a failure, in the order they were given; empty on success.</summary>
    public ImmutableArray<Error> Errors { get; }

    public bool IsSuccess => Errors.IsEmpty;

    public bool IsFailure => !IsSuccess;

    public static Result Success() => SuccessResult;

    /// <summary>
    /// A success holding <paramref name="value"/>. The implicit conversion from a value uses it; call it directly
    /// when the value's static type is an interface, which C# never converts implicitly.
    /// </summary>
    public static Result<T> Success<T>(T value)
        where T : notnull => new(value);

    public static implicit operator Result(Error error) => new(OneError(error));

    public static implicit operator Result(ImmutableArray<Error> errors) => new(FailureErrors(errors));

    private protected static ImmutableArray<Error> OneError(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return [error];
    }

    // A failure without an Error would read as a success, so an empty or null list is a bug.
    private protected static ImmutableArray<Error> FailureErrors(ImmutableArray<Error> errors)
    {
        if (errors.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A failed Result needs at least one Error.", nameof(errors));
        }

        if (errors.Contains(null!))
        {
            throw new ArgumentException("A failed Result cannot contain a null Error.", nameof(errors));
        }

        return errors;
    }
}

/// <summary>The outcome of an Operation that yields a <typeparamref name="T"/> on success.</summary>
public sealed class Result<T> : Result
    where T : notnull
{
    private readonly T? _value;

    // Every success is built here, so this is the one place that keeps null out of a success.
    internal Result(T value)
        : base([])
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value), "Null is never a successful Result.");
        }

        _value = value;
    }

    private Result(ImmutableArray<Error> errors)
        : base(errors) { }

    /// <summary>The value of a success. Reading it from a failure is a bug and throws.</summary>
    public T Value =>
        IsSuccess ? _value! : throw new InvalidOperationException("A failed Result has no value; read its Errors.");

    public static implicit operator Result<T>(T value) => Success(value);

    public static implicit operator Result<T>(Error error) => new(OneError(error));

    public static implicit operator Result<T>(ImmutableArray<Error> errors) => new(FailureErrors(errors));
}
