using Microsoft.Extensions.DependencyInjection;
using OptionB.ModuleWithGenerator;
using OptionB.SelfGenerated.Alpha;
using Spike.Common;
using Xunit;

namespace OptionB.Tests;

// Generator inside the Module, no InternalsVisibleTo, no generator scan of it in the Host. Alpha registers
// first, so ISender belongs to Alpha; the Things Module still works because it resolves its own concrete
// generated Mediator class, which each generated AddMediator adds with a plain Add.
public sealed class GeneratorInModuleBehaviourTests
{
    private static ServiceProvider BuildHost() =>
        new ServiceCollection().AddAlphaModule().AddThingsModule()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

    [Fact]
    public async Task Command_runs_logging_authorization_validation_and_unit_of_work_in_order()
    {
        await using var host = BuildHost();
        using var scope = host.CreateScope();

        var result = await ThingsGateway.CreateThing(scope.ServiceProvider, "a");

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

        var result = await ThingsGateway.GetThing(scope.ServiceProvider, Guid.CreateVersion7());

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

        var result = await ThingsGateway.RunOuter(scope.ServiceProvider, nested);

        Assert.Equal("outer done", result.Value);
        var steps = scope.ServiceProvider.GetRequiredService<OperationTrace>().Steps;
        Assert.Equal(["uow:rollback", "log:end RunOuterCommand"], steps.TakeLast(2));
        Assert.DoesNotContain("uow:commit", steps);
    }

    [Fact]
    public async Task Successful_nested_command_joins_the_outer_unit_of_work_which_commits_once()
    {
        await using var host = BuildHost();
        using var scope = host.CreateScope();

        var result = await ThingsGateway.RunOuter(scope.ServiceProvider, NestedOutcome.Succeeds);

        Assert.Equal("outer done", result.Value);
        Assert.Equal(
        [
            "log:start RunOuterCommand", "uow:begin", "handle RunOuterCommand",
            "log:start RunNestedCommand", "uow:join", "handle RunNestedCommand", "log:end RunNestedCommand",
            "uow:commit", "log:end RunOuterCommand",
        ], scope.ServiceProvider.GetRequiredService<OperationTrace>().Steps);
    }
}
