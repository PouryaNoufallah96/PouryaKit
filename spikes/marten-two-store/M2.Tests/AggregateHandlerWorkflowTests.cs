using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.Marten;

namespace M2.Tests;

public sealed class Tab
{
    public Guid Id { get; set; }

    public decimal Total { get; set; }

    public void Apply(TabOpened opened) => Id = opened.TabId;

    public void Apply(TabCharged charged) => Total += charged.Amount;
}

public sealed record TabOpened(Guid TabId);

public sealed record TabCharged(Guid TabId, decimal Amount);

public sealed record ChargeTab(Guid TabId, decimal Amount);

public static class ChargeTabHandler
{
    [AggregateHandler]
    public static TabCharged Handle(ChargeTab command, Tab tab) => new(command.TabId, command.Amount);
}

internal sealed record ChargeInternalTab(Guid TabId, decimal Amount);

internal static class ChargeInternalTabHandler
{
    [AggregateHandler]
    public static TabCharged Handle(ChargeInternalTab command, Tab tab) => new(command.TabId, command.Amount);
}

// The rejected option, one honest attempt: Wolverine's aggregate-handler workflow as the Stream's commit owner, in a
// plain Wolverine Host with Marten integrated. It commits the event through Wolverine's own generated handler chain, run
// by IMessageBus: the Kit's IDispatcher, its pipeline and its Unit of Work never run it. And Wolverine takes only public
// handler types, so the internal handler of a Module's internal command (ADR 0001) is refused when it is declared.
public sealed class AggregateHandlerWorkflowTests(Postgres postgres) : SpikeTest(postgres)
{
    [Fact]
    public async Task The_aggregate_handler_workflow_commits_through_wolverines_own_chain_and_refuses_an_internal_handler()
    {
        var refused = Assert.Throws<ArgumentOutOfRangeException>(() => BuildWolverineHost(typeof(ChargeInternalTabHandler)));
        using var host = BuildWolverineHost(typeof(ChargeTabHandler));
        await host.StartAsync(Token);
        var tabId = Guid.CreateVersion7();
        await using (var session = host.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            session.Events.StartStream<Tab>(tabId, new TabOpened(tabId));
            await session.SaveChangesAsync(Token);
        }

        await host.Services.GetRequiredService<IMessageBus>().InvokeAsync(new ChargeTab(tabId, 5), Token);

        Assert.StartsWith("Handler types must be public", refused.Message, StringComparison.Ordinal);
        Assert.Equal(1, await CountAsync("select count(*) from events.mt_events where stream_id = $1 and type = 'tab_charged'", tabId));
        await host.StopAsync(Token);
    }

    private IHost BuildWolverineHost(params Type[] handlers)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddMarten(options =>
            {
                options.Connection(ConnectionString);
                options.DatabaseSchemaName = "events";
            })
            .ApplyAllDatabaseChangesOnStartup()
            .IntegrateWithWolverine();
        builder.UseWolverine(wolverine =>
        {
            wolverine.Discovery.DisableConventionalDiscovery();
            foreach (var handler in handlers)
            {
                wolverine.Discovery.IncludeType(handler);
            }

            wolverine.Policies.AutoApplyTransactions();
        });
        return builder.Build();
    }
}
