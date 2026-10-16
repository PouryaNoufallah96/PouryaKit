using System.Runtime.CompilerServices;
using M2.Contracts;
using M2.Marten;
using Microsoft.EntityFrameworkCore;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Messaging;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Modularity;
using PouryaKit.Persistence.EFCore;
using PouryaKit.Persistence.Postgres;

[assembly: InternalsVisibleTo("M2.Tests")]

namespace M2.Catalog;

/// <summary>The Catalog EF Module's Composition Root.</summary>
public sealed class CatalogModule : IModuleDefinition
{
    /// <inheritdoc />
    public void Configure(ModuleBuilder module)
    {
        module.UsePouryaKitPostgres<CatalogDbContext>(connectionName: "app", schema: "catalog");
        module.UseSpikeEfStores();
        module.AddPouryaKitCqrs();
        module.Subscribe<AccountOpenedV1, NoteAccountOpened>("catalog.account-opened");
    }
}

internal sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : PouryaKitDbContext(options);

[Idempotent]
internal sealed record TouchCommand : Command;

internal sealed class TouchHandler : CommandHandler<TouchCommand>
{
    public override Task<Result> Handle(TouchCommand command, CancellationToken cancellationToken) => Task.FromResult(Result.Success());
}

internal sealed class NoteAccountOpened : IIntegrationEventHandler<AccountOpenedV1>
{
    public Task<Result> Handle(AccountOpenedV1 integrationEvent, CancellationToken cancellationToken) => Task.FromResult(Result.Success());
}
