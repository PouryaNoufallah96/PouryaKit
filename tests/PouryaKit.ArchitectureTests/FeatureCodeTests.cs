using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Offending;
using Offending.Contracts;

namespace PouryaKit.ArchitectureTests;

// Rules 2, 3 and 5: what a Module's code never does. Each rule judges the Proving Sample, then the offending fixture,
// and a Kit package where the call is known to be, so a rule that reads nothing fails.
public sealed class FeatureCodeTests
{
    private static readonly Assembly OffendingModuleAssembly = typeof(OffendingModule).Assembly;

    [Fact]
    public void No_module_saves_commits_or_touches_its_database_and_only_the_unit_of_work_does()
    {
        Assert.Empty(Sample.Modules.SelectMany(ModuleRules.SavesOrCommits));
        var kitSaves = ModuleRules.SavesOrCommits(Kit("PouryaKit.Persistence.EFCore")).ToList();
        Assert.Contains("PouryaKit.Persistence.EFCore.EfUnitOfWork calls DbContext.SaveChangesAsync.", kitSaves);
        Assert.Contains("PouryaKit.Persistence.EFCore.EfUnitOfWork calls IUnitOfWorkOutbox.WriteAsync.", kitSaves);
    }

    [Fact]
    public void A_module_that_saves_commits_writes_in_bulk_or_runs_a_kit_step_is_rejected()
    {
        var violations = ModuleRules.SavesOrCommits(OffendingModuleAssembly);

        Assert.Equal(
            [
                "Offending.BulkWritingCommandHandler calls EntityFrameworkQueryableExtensions.ExecuteDelete.",
                "Offending.BulkWritingCommandHandler calls EntityFrameworkQueryableExtensions.ExecuteUpdateAsync.",
                "Offending.CommittingCommandHandler calls DbConnection.BeginTransactionAsync.",
                "Offending.CommittingCommandHandler calls DbTransaction.CommitAsync.",
                "Offending.CommittingCommandHandler runs the SQL statement \"commit\".",
                "Offending.CommittingCommandHandler runs the SQL statement \"select 1;\n  END ;\".",
                "Offending.CommittingCommandHandler runs the SQL statement \"update offending_marks set seen = true; abort work\".",
                "Offending.ConnectingCommandHandler calls DbContext.get_Database.",
                "Offending.KitStepCallingCommandHandler calls DomainEventDrain.RunAsync.",
                "Offending.KitStepCallingCommandHandler calls EfUnitOfWorkDbContext.Of.",
                "Offending.KitStepCallingCommandHandler calls IDelayedCommandOutbox.Add.",
                "Offending.KitStepCallingCommandHandler calls IIdempotencyStore.RecordAsync.",
                "Offending.KitStepCallingCommandHandler calls IIdempotencyStore.ReserveAsync.",
                "Offending.KitStepCallingCommandHandler calls IIntegrationEventOutbox.Add.",
                "Offending.KitStepCallingCommandHandler calls IQueryCache.EvictAsync.",
                "Offending.KitStepCallingCommandHandler calls IReadingStateGuard.OnlyReading.",
                "Offending.KitStepCallingCommandHandler calls IReceipts.RecordAsync.",
                "Offending.KitStepCallingCommandHandler calls ISnapshotMappers.Map.",
                "Offending.KitStepCallingCommandHandler calls IUnitOfWork.RunAsync.",
                "Offending.KitStepCallingCommandHandler calls IUnitOfWorkOutbox.WriteAsync.",
                "Offending.KitStepCallingCommandHandler calls Operation.OnlyReading.",
                "Offending.KitStepCallingCommandHandler calls OperationContextServiceProviderExtensions.BeginOperation.",
                "Offending.KitStepCallingCommandHandler calls TrackedState.Capture.",
                "Offending.KitStepCallingCommandHandler calls TrackedState.ThrowIfChanged.",
                "Offending.SavingCommandHandler calls DbContext.SaveChangesAsync.",
            ],
            violations.Order(StringComparer.Ordinal)
        );
    }

    [Theory]
    [InlineData("begin transaction isolation level serializable, read write", true)]
    [InlineData("insert into marks values (1); COMMIT AND CHAIN;", true)]
    [InlineData("rollback to savepoint before_marks", true)]
    [InlineData("prepare transaction 'offending'", true)]
    [InlineData("-- note\ncommit", true)]
    [InlineData("/* x */ commit", true)]
    [InlineData("commit -- done", true)]
    [InlineData("select 1; /* then */ end transaction", true)]
    [InlineData("do $$ begin perform 1; end $$", false)]
    [InlineData("do $$ begin perform 1; end; $$", false)]
    [InlineData("create function touch() returns void language plpgsql as $body$ begin perform 1; end; $body$", false)]
    [InlineData("select 'a;commit;b'", false)]
    [InlineData("select 1 -- ; commit", false)]
    [InlineData("Abort; retry?", false)]
    [InlineData("select ended_at from marks where committed", false)]
    public void Transaction_control_is_found_at_any_statement_boundary_outside_comments_and_strings(string sql, bool isTransactionControl)
    {
        Assert.Equal(isTransactionControl, ModuleRules.HoldsTransactionControl(sql));
    }

    [Fact]
    public void No_module_contract_or_messaging_contract_names_a_transport_type()
    {
        Assert.Empty(
            Sample.Modules.Concat(Sample.Contracts).Append(Kit("PouryaKit.Messaging")).SelectMany(ModuleRules.TransportTypesUsedBy)
        );
        Assert.NotEmpty(ModuleRules.TransportTypesUsedBy(Kit("PouryaKit.Messaging.Wolverine")));
    }

    [Fact]
    public void A_module_that_names_a_transport_type_is_rejected()
    {
        Assert.Contains("Offending uses Wolverine.IMessageBus from Wolverine.", ModuleRules.TransportTypesUsedBy(OffendingModuleAssembly));
        Assert.Empty(ModuleRules.TransportTypesUsedBy(typeof(OffendingCreatedV1).Assembly));
    }

    [Fact]
    public void No_module_dbcontext_maps_the_message_stores_envelopes()
    {
        var models = Sample.Models.Values.SelectMany(models => models).ToList();

        Assert.Equal(3, models.Count);
        Assert.Empty(models.SelectMany(model => ModuleRules.EnvelopeStorageIn("a Module's DbContext", model)));
    }

    [Fact]
    public void A_dbcontext_that_maps_the_message_stores_envelopes_is_rejected()
    {
        var options = new DbContextOptionsBuilder().UseNpgsql("Host=localhost;Database=architecture-tests").Options;
        var contextType = OffendingModuleAssembly.GetType("Offending.OffendingDbContext", throwOnError: true)!;
        using var context = (DbContext)Activator.CreateInstance(contextType, [options])!;

        var violations = ModuleRules.EnvelopeStorageIn("OffendingDbContext", context.GetService<IDesignTimeModel>().Model).ToList();

        Assert.Contains(violations, violation => violation.Contains("wolverine.wolverine_incoming_envelopes", StringComparison.Ordinal));
        Assert.Contains(violations, violation => violation.Contains("wolverine.wolverine_outgoing_envelopes", StringComparison.Ordinal));
    }

    [Fact]
    public void Only_the_kits_domain_event_drain_clears_domain_events()
    {
        Assert.Empty(Sample.Modules.SelectMany(ModuleRules.ClearsDomainEvents));
        Assert.Equal(["PouryaKit.Cqrs.DomainEventDrain"], Sample.KitPackages.SelectMany(ModuleRules.ClearsDomainEvents));
    }

    [Fact]
    public void A_module_that_clears_domain_events_is_rejected()
    {
        Assert.Equal(["Offending.ClearingDomainEventHandler"], ModuleRules.ClearsDomainEvents(OffendingModuleAssembly));
    }

    private static Assembly Kit(string name) => Assert.Single(Sample.KitPackages, package => package.GetName().Name == name);
}
