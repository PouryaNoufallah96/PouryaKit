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
        Assert.Contains(
            "PouryaKit.Persistence.EFCore.EfUnitOfWork calls DbContext.SaveChangesAsync.",
            ModuleRules.SavesOrCommits(Kit("PouryaKit.Persistence.EFCore"))
        );
    }

    [Fact]
    public void A_module_that_saves_commits_or_touches_its_database_is_rejected()
    {
        var violations = ModuleRules.SavesOrCommits(OffendingModuleAssembly);

        Assert.Equal(
            [
                "Offending.CommittingCommandHandler calls DbConnection.BeginTransactionAsync.",
                "Offending.CommittingCommandHandler calls DbTransaction.CommitAsync.",
                "Offending.CommittingCommandHandler runs the SQL statement \"commit\".",
                "Offending.ConnectingCommandHandler calls DbContext.get_Database.",
                "Offending.SavingCommandHandler calls DbContext.SaveChangesAsync.",
            ],
            violations.Order(StringComparer.Ordinal)
        );
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
