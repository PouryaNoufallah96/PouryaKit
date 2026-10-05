using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PouryaKit.Cqrs;
using PouryaKit.Domain;

namespace PouryaKit.Validation.Tests;

internal sealed record RegisterProductCommand(string Name, int Quantity) : Command<Guid>;

// Two validators for one command: the step must return the failures of both.
internal sealed class RegisterProductNameValidator : AbstractValidator<RegisterProductCommand>
{
    public RegisterProductNameValidator() => RuleFor(x => x.Name).NotEmpty();
}

internal sealed class RegisterProductQuantityValidator : AbstractValidator<RegisterProductCommand>
{
    public RegisterProductQuantityValidator() =>
        RuleFor(x => x.Quantity).GreaterThan(0).WithErrorCode("Product.QuantityPositive");
}

internal sealed class RegisterProductHandler(HandledCommands handled) : CommandHandler<RegisterProductCommand, Guid>
{
    public override Task<Result<Guid>> Handle(RegisterProductCommand command, CancellationToken cancellationToken)
    {
        handled.Count++;
        return Task.FromResult<Result<Guid>>(Guid.CreateVersion7());
    }
}

internal sealed record ArchiveProductCommand(Guid ProductId) : Command;

// Misuse under test: a validator that sends a command runs before the Unit of Work starts.
internal sealed class ArchiveProductValidator : AbstractValidator<ArchiveProductCommand>
{
    public ArchiveProductValidator(IDispatcher dispatcher) =>
        RuleFor(x => x.ProductId)
            .MustAsync(async (id, ct) =>
                (await dispatcher.SendAsync(new RegisterProductCommand("Lamp", 1), ct)).IsSuccess
            );
}

internal sealed class ArchiveProductHandler(HandledCommands handled) : CommandHandler<ArchiveProductCommand>
{
    public override Task<Result> Handle(ArchiveProductCommand command, CancellationToken cancellationToken)
    {
        handled.Count++;
        return Task.FromResult(Result.Success());
    }
}

internal sealed class HandledCommands
{
    public int Count { get; set; }
}

internal sealed class PassThroughUnitOfWork : IUnitOfWork
{
    public Task<Result> RunAsync(Func<CancellationToken, Task<Result>> command, CancellationToken cancellationToken) =>
        command(cancellationToken);
}

public sealed class ValidationTests
{
    [Fact]
    public async Task Every_failure_of_every_validator_comes_back_as_one_validation_result_and_the_handler_never_runs()
    {
        await using var host = BuildHost();
        using var scope = host.CreateScope();

        var result = await Dispatcher(scope)
            .SendAsync(new RegisterProductCommand("", 0), TestContext.Current.CancellationToken);

        Assert.Equal(
            [("NotEmptyValidator", "Name"), ("Product.QuantityPositive", "Quantity")],
            result.Errors.Select(e => (e.Code, e.Target)).Order()
        );
        Assert.All(result.Errors, e => Assert.Equal(ErrorKind.Validation, e.Kind));
        Assert.Equal(0, scope.ServiceProvider.GetRequiredService<HandledCommands>().Count);
    }

    [Fact]
    public async Task A_valid_command_reaches_its_handler()
    {
        await using var host = BuildHost();
        using var scope = host.CreateScope();

        var result = await Dispatcher(scope)
            .SendAsync(new RegisterProductCommand("Lamp", 3), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, scope.ServiceProvider.GetRequiredService<HandledCommands>().Count);
    }

    [Fact]
    public async Task A_validator_that_sends_a_command_throws_before_any_handler_runs()
    {
        await using var host = BuildHost();
        using var scope = host.CreateScope();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Dispatcher(scope).SendAsync(new ArchiveProductCommand(Guid.CreateVersion7()), TestContext.Current.CancellationToken)
        );

        Assert.Equal(0, scope.ServiceProvider.GetRequiredService<HandledCommands>().Count);
    }

    private static ServiceProvider BuildHost() =>
        new ServiceCollection()
            .AddPouryaKitCqrs(typeof(ValidationTests).Assembly)
            .AddPouryaKitValidation(typeof(ValidationTests).Assembly)
            .AddScoped<HandledCommands>()
            .AddScoped<IUnitOfWork, PassThroughUnitOfWork>()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

    private static IDispatcher Dispatcher(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<IDispatcher>();
}
