using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Security.Tests.Contracts;
using PouryaKit.Security.Tests.Module;

namespace PouryaKit.Security.Tests;

public sealed class RegistrationTests
{
    [Fact]
    public void A_permission_declared_on_a_type_that_is_not_one_concrete_command_or_query_fails_registration()
    {
        var thrown = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddPouryaKitPermissions(typeof(RegistrationTests).Assembly)
        );

        Assert.Equal(
            [typeof(AbstractCommand).FullName, typeof(GenericCommand<>).FullName, typeof(NotAnOperation).FullName],
            thrown.Message.Split(Environment.NewLine).Select(line => line.Split(' ')[0]).Order(StringComparer.Ordinal)
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_command_declared_in_another_assembly_and_handled_in_the_scanned_one_is_permission_checked(bool granted)
    {
        var module = typeof(HandledCommands).Assembly;
        await using var host = new ServiceCollection()
            .AddPouryaKitPermissions(module)
            .AddPouryaKitCqrs(module)
            .AddLogging()
            .AddScoped<HandledCommands>()
            .AddScoped<IUnitOfWork, UnitOfWorkStandIn>()
            .AddSingleton<IActorPermissions>(new ActorPermissionsStandIn(granted ? [RevokeAccessCommand.Permission] : []))
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var scope = host.CreateScope();
        scope.ServiceProvider.BeginOperation(new OperationContext(Actor.Person("ada")));

        var result = await scope
            .ServiceProvider.GetRequiredService<IDispatcher>()
            .SendAsync(new RevokeAccessCommand(), TestContext.Current.CancellationToken);

        var handled = scope.ServiceProvider.GetRequiredService<HandledCommands>().Commands;
        if (granted)
        {
            Assert.True(result.IsSuccess);
            Assert.Equal([typeof(RevokeAccessCommand)], handled);
        }
        else
        {
            Assert.Equal(PermissionErrors.Denied, Assert.Single(result.Errors));
            Assert.Empty(handled);
        }
    }
}

internal sealed class ActorPermissionsStandIn(HashSet<string> held) : IActorPermissions
{
    public Task<bool> HasPermissionAsync(OperationContext context, string permission, CancellationToken cancellationToken) =>
        Task.FromResult(held.Contains(permission));
}

internal sealed class UnitOfWorkStandIn : IUnitOfWork
{
    public Task<Result> RunAsync(Func<CancellationToken, Task<Result>> command, CancellationToken cancellationToken) =>
        command(cancellationToken);
}

[RequiresPermission("tests.declared")]
internal sealed record DeclaredCommand : Command;

[RequiresPermission("tests.abstract")]
internal abstract record AbstractCommand : Command;

[RequiresPermission("tests.generic")]
internal sealed record GenericCommand<T> : Command;

[RequiresPermission("tests.not-an-operation")]
internal sealed class NotAnOperation;
