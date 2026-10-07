using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PouryaKit.Cqrs.Tests.Module;
using PouryaKit.Domain;

namespace PouryaKit.Cqrs.Tests;

internal static class TestHost
{
    private static readonly ServiceProviderOptions Strict = new() { ValidateOnBuild = true, ValidateScopes = true };

    /// <summary>The Host wiring for the test Module: one call with its assembly, plus the stand-ins.</summary>
    public static ServiceProvider Build(bool withValidationStep = true, Type? unitOfWork = null, bool withIdempotencyStore = false)
    {
        var services = new ServiceCollection()
            .AddPouryaKitCqrs(typeof(ThingsGateway).Assembly)
            .AddScoped<OperationTrace>()
            .AddScoped(typeof(IUnitOfWork), unitOfWork ?? typeof(UnitOfWorkStandIn))
            .AddScoped(typeof(ILogger<>), typeof(TraceLogger<>));
        if (withValidationStep)
        {
            services.AddScoped(typeof(IValidationStep<>), typeof(ValidationStepStandIn<>));
        }

        if (withIdempotencyStore)
        {
            services.AddScoped<IIdempotencyStore, IdempotencyStoreStandIn>();
        }

        return services.BuildServiceProvider(Strict);
    }
}

/// <summary>Records each key it reserves and each response it records, and holds no key a committed Operation recorded.</summary>
internal sealed class IdempotencyStoreStandIn(OperationTrace trace) : IIdempotencyStore
{
    public Task<RecordedSuccess?> ReserveAsync(IdempotencyKey key, CancellationToken cancellationToken)
    {
        trace.Add($"reserve {key.Command} {key.RequestId}");
        return Task.FromResult<RecordedSuccess?>(null);
    }

    public Task RecordAsync(IdempotencyKey key, string response, CancellationToken cancellationToken)
    {
        trace.Add($"record {key.Command} {response}");
        return Task.CompletedTask;
    }
}

/// <summary>Follows the <see cref="IUnitOfWork"/> contract and records whether it committed or rolled back.</summary>
internal sealed class UnitOfWorkStandIn(Operation operation, OperationTrace trace) : IUnitOfWork
{
    public async Task<Result> RunAsync(Func<CancellationToken, Task<Result>> command, CancellationToken cancellationToken)
    {
        trace.Add("uow:begin");
        var result = await command(cancellationToken);
        var commits = result.IsSuccess && !operation.IsRollbackOnly;
        trace.Add(commits ? "uow:commit" : "uow:rollback");
        return result;
    }
}

/// <summary>Breaks the <see cref="IUnitOfWork"/> contract: reports a success that is not the command's Result.</summary>
internal sealed class SuccessReportingUnitOfWork : IUnitOfWork
{
    public async Task<Result> RunAsync(Func<CancellationToken, Task<Result>> command, CancellationToken cancellationToken)
    {
        await command(cancellationToken);
        return Result.Success();
    }
}

/// <summary>
/// Breaks the <see cref="IUnitOfWork"/> contract: after the command, a later step sends a nested command that fails, as
/// a Domain Event Handler may, and the Unit of Work reports the command's success without checking the Operation again.
/// </summary>
internal sealed class NoRecheckUnitOfWork(IDispatcher dispatcher) : IUnitOfWork
{
    public async Task<Result> RunAsync(Func<CancellationToken, Task<Result>> command, CancellationToken cancellationToken)
    {
        var result = await command(cancellationToken);
        _ = await ThingsGateway.RunNested(dispatcher, NestedOutcome.HandlerFails);
        return result;
    }
}

/// <summary>Stands in for the validation package: records the step and fails an invalid operation.</summary>
internal sealed class ValidationStepStandIn<TOperation>(OperationTrace trace) : IValidationStep<TOperation>
    where TOperation : notnull
{
    public Task<Result> ValidateAsync(TOperation operation, CancellationToken cancellationToken)
    {
        trace.Add($"validate {typeof(TOperation).Name}");
        return Task.FromResult<Result>(
            operation is IValidatedOperation { IsValid: false }
                ? Error.Validation("Nested.ValidationFails", "Invalid.", "Outcome")
                : Result.Success()
        );
    }
}

/// <summary>Records each log entry by its event name, the operation it names and its exception type, if any.</summary>
internal sealed class TraceLogger<T>(OperationTrace trace) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    )
    {
        var operation = state is IEnumerable<KeyValuePair<string, object?>> values
            ? values.FirstOrDefault(value => value.Key == "Operation").Value
            : null;
        trace.Add(
            exception is null
                ? $"log:{eventId.Name} {operation}"
                : $"log:{eventId.Name} {operation} {exception.GetType().Name}"
        );
    }
}
