using Microsoft.Extensions.DependencyInjection;

namespace Spike.Common;

// Shared by both options so that the owned-code comparison counts dispatch code only.

public enum ErrorKind { Validation, NotFound, Conflict, Forbidden, Unexpected }

public sealed record Error(ErrorKind Kind, string Code);

public interface IResult
{
    bool IsFailure { get; }
    IReadOnlyList<Error> Errors { get; }
}

// Lets a generic pipeline step build a failed TResult without reflection.
public interface IFailable<TSelf> where TSelf : IFailable<TSelf>
{
    static abstract TSelf Fail(IReadOnlyList<Error> errors);
}

public sealed class Result<T> : IResult, IFailable<Result<T>>
{
    private readonly T? _value;

    private Result(T value) { _value = value; Errors = []; }
    private Result(IReadOnlyList<Error> errors) { Errors = errors; }

    public bool IsFailure => Errors.Count > 0;
    public IReadOnlyList<Error> Errors { get; }
    public T Value => IsFailure ? throw new InvalidOperationException("A failed Result has no value.") : _value!;

    public static Result<T> Success(T value) => new(value);

    public static Result<T> Fail(IReadOnlyList<Error> errors) =>
        errors.Count == 0 ? throw new ArgumentException("A failed Result needs an Error.", nameof(errors)) : new(errors);

    public static implicit operator Result<T>(T value) => Success(value);
    public static implicit operator Result<T>(Error error) => Fail([error]);
}

/// <summary>Validation step input: every Error returned is collected before the handler runs.</summary>
public interface IValidator<in TOperation>
{
    IEnumerable<Error> Validate(TOperation operation);
}

/// <summary>Optional authorization hook: null allows the Operation.</summary>
public interface IAuthorizer<in TOperation>
{
    Error? Authorize(TOperation operation);
}

/// <summary>Per-scope state of one Operation, shared by every nested dispatch inside it.</summary>
public sealed class OperationState
{
    public int Depth { get; set; }
    public bool IsRollbackOnly { get; private set; }
    public void MarkRollbackOnly() => IsRollbackOnly = true;
}

/// <summary>Records what ran, in order, so tests can assert the pipeline sequence.</summary>
public sealed class OperationTrace
{
    private readonly List<string> _steps = [];
    public IReadOnlyList<string> Steps => _steps;
    public void Add(string step) => _steps.Add(step);
}

/// <summary>Unit of Work stand-in: the outermost command owns the outcome, nested commands join it.</summary>
public sealed class UnitOfWorkStandIn(OperationState state, OperationTrace trace)
{
    public bool Begin()
    {
        if (state.Depth > 1)
        {
            trace.Add("uow:join");
            return false;
        }

        trace.Add("uow:begin");
        return true;
    }

    public void Complete(IResult result) =>
        trace.Add(result.IsFailure || state.IsRollbackOnly ? "uow:rollback" : "uow:commit");
}

public static class CommonRegistration
{
    public static IServiceCollection AddOperationStandIns(this IServiceCollection services) =>
        services.AddScoped<OperationState>().AddScoped<OperationTrace>().AddScoped<UnitOfWorkStandIn>();
}
