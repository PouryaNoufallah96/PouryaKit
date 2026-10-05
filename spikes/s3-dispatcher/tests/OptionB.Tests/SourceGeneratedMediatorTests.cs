using Mediator;
using Microsoft.Extensions.DependencyInjection;
using OptionB.Module;
using OptionB.Pipeline;
using Spike.Common;
using Xunit;

namespace OptionB.Tests;

public sealed class SourceGeneratedMediatorTests
{
    private static readonly ServiceProviderOptions Strict = new() { ValidateOnBuild = true, ValidateScopes = true };

    private static ServiceProvider BuildHost()
    {
        var services = new ServiceCollection();
        services.AddMediator(options =>
        {
            options.Assemblies = [typeof(ThingsGateway)];
            // The default (Singleton) fails ValidateOnBuild: "Cannot consume scoped service ... from singleton".
            options.ServiceLifetime = ServiceLifetime.Scoped;
        });
        services.AddKitPipeline(typeof(ThingsGateway).Assembly);
        return services.BuildServiceProvider(Strict);
    }

    [Fact]
    public async Task Command_runs_logging_authorization_validation_and_unit_of_work_in_order()
    {
        await using var host = BuildHost();
        using var scope = host.CreateScope();

        var result = await ThingsGateway.CreateThing(scope.ServiceProvider.GetRequiredService<ISender>(), "a");

        Assert.False(result.IsFailure);
        Assert.Equal(
        [
            "log:start CreateThingCommand", "authorize CreateThingCommand", "validate CreateThingCommand",
            "uow:begin", "handle CreateThingCommand", "uow:commit", "log:end CreateThingCommand",
        ], scope.ServiceProvider.GetRequiredService<OperationTrace>().Steps);
    }

    [Fact]
    public async Task Query_runs_the_same_pipeline_without_the_unit_of_work()
    {
        await using var host = BuildHost();
        using var scope = host.CreateScope();

        var result = await ThingsGateway.GetThing(scope.ServiceProvider.GetRequiredService<ISender>(), Guid.CreateVersion7());

        Assert.Equal("thing", result.Value);
        Assert.Equal(
        [
            "log:start GetThingQuery", "authorize GetThingQuery", "validate GetThingQuery",
            "handle GetThingQuery", "log:end GetThingQuery",
        ], scope.ServiceProvider.GetRequiredService<OperationTrace>().Steps);
    }

    [Theory]
    [InlineData(NestedOutcome.HandlerFails)]
    [InlineData(NestedOutcome.ValidationFails)]
    [InlineData(NestedOutcome.AuthorizationFails)]
    public async Task Ignored_nested_failure_of_any_kind_rolls_the_operation_back(NestedOutcome nested)
    {
        await using var host = BuildHost();
        using var scope = host.CreateScope();

        var result = await ThingsGateway.RunOuter(scope.ServiceProvider.GetRequiredService<ISender>(), nested);

        Assert.Equal("outer done", result.Value); // the outer handler ignored the nested failure
        var steps = scope.ServiceProvider.GetRequiredService<OperationTrace>().Steps;
        Assert.Equal(["uow:rollback", "log:end RunOuterCommand"], steps.TakeLast(2));
        Assert.DoesNotContain("uow:commit", steps);
    }

    [Fact]
    public async Task Successful_nested_command_joins_the_outer_unit_of_work_which_commits_once()
    {
        await using var host = BuildHost();
        using var scope = host.CreateScope();

        var result = await ThingsGateway.RunOuter(scope.ServiceProvider.GetRequiredService<ISender>(), NestedOutcome.Succeeds);

        Assert.Equal("outer done", result.Value);
        Assert.Equal(
        [
            "log:start RunOuterCommand", "uow:begin", "handle RunOuterCommand",
            "log:start RunNestedCommand", "uow:join", "handle RunNestedCommand", "log:end RunNestedCommand",
            "uow:commit", "log:end RunOuterCommand",
        ], scope.ServiceProvider.GetRequiredService<OperationTrace>().Steps);
    }

    // OrphanCommand has no handler. The Host build only warns (MSG0005) and the strict container builds;
    // the failure surfaces on the first send.
    [Fact]
    public async Task Missing_handler_is_only_a_build_warning_and_fails_on_first_send()
    {
        await using var host = BuildHost();
        using var scope = host.CreateScope();

        await Assert.ThrowsAsync<MissingMessageHandlerException>(
            async () => await OrphanGateway.SendOrphan(scope.ServiceProvider.GetRequiredService<ISender>()));
    }
}
