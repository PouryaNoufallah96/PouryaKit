using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;

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
}

[RequiresPermission("tests.declared")]
internal sealed record DeclaredCommand : Command;

[RequiresPermission("tests.abstract")]
internal abstract record AbstractCommand : Command;

[RequiresPermission("tests.generic")]
internal sealed record GenericCommand<T> : Command;

[RequiresPermission("tests.not-an-operation")]
internal sealed class NotAnOperation;
