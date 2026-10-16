using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PouryaKit.Cqrs.Tests.Contracts;
using PouryaKit.Cqrs.Tests.Module;

namespace PouryaKit.Cqrs.Tests;

public sealed class TenancyTests
{
    [Fact]
    public async Task A_command_declared_in_another_assembly_and_handled_in_a_tenant_scoped_one_needs_a_tenant()
    {
        var module = typeof(ThingsGateway).Assembly;
        await using var host = new ServiceCollection()
            .AddPouryaKitTenancy(module)
            .AddPouryaKitCqrs(module)
            .AddScoped<OperationTrace>()
            .AddScoped<IUnitOfWork, UnitOfWorkStandIn>()
            .AddScoped(typeof(ILogger<>), typeof(TraceLogger<>))
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var scope = host.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<IDispatcher>().SendAsync(new ArchiveThingCommand(), TestContext.Current.CancellationToken);

        Assert.Equal(TenantErrors.Required.Code, Assert.Single(result.Errors).Code);
        Assert.DoesNotContain("handle ArchiveThingCommand", scope.ServiceProvider.GetRequiredService<OperationTrace>().Steps);
    }
}
