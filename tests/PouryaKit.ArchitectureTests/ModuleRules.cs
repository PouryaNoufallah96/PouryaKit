using System.Data;
using System.Data.Common;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Modularity;
using PouryaKit.Persistence.EFCore;

namespace PouryaKit.ArchitectureTests;

/// <summary>
/// The structure rules, each a function from an assembly (or a model) to its violations, so the same function judges
/// the Proving Sample and the offending fixture. A consuming project can take them as they are; Wave 1 ships no rules
/// package.
/// </summary>
internal static partial class ModuleRules
{
    /// <summary>
    /// ADR 0001: a Module reaches another only through that Module's Contracts. Any reference to another Module's
    /// implementation fails, direct or through other projects. The rule reads the project's restored closure, so a
    /// project or package reference counts even when no emitted code uses it, and the compiled assembly's references,
    /// for an assembly referenced by file. Not seen: a Module that loads another's assembly or resolves its services at
    /// run time by name, without referencing it; and a reference to a Module whose assembly name differs from its
    /// project name, since the restored closure names projects and the rule compares assembly names.
    /// </summary>
    public static IEnumerable<string> ReferencesToOtherModules(Assembly module, RestoredPackage restored, IReadOnlySet<string> modules) =>
        from reached in ReachedBy(module, restored)
        where reached != Name(module) && modules.Contains(reached)
        select $"{Name(module)} references {reached}, the implementation of another Module; reach it through {reached}.Contracts.";

    /// <summary>
    /// Contracts are plain records other Modules compile against: nothing of the Kit, as a project or a package, and no
    /// Module, read the same way as <see cref="ReferencesToOtherModules"/>.
    /// </summary>
    public static IEnumerable<string> ReferencesOfContracts(Assembly contracts, RestoredPackage restored, IReadOnlySet<string> modules) =>
        from reached in ReachedBy(contracts, restored)
        where reached.StartsWith("PouryaKit", StringComparison.Ordinal) || modules.Contains(reached)
        select $"{Name(contracts)} references {reached}; Contracts reference nothing from the Kit and no Module.";

    private static IEnumerable<string> ReachedBy(Assembly assembly, RestoredPackage restored) =>
        restored
            .Projects.Concat(restored.Packages.Select(package => package.Id))
            .Concat(Sample.ProjectReferencesOf(assembly).Select(Name))
            .Concat(assembly.GetReferencedAssemblies().Select(reference => reference.Name!))
            .Distinct()
            .Order(StringComparer.Ordinal);

    /// <summary>
    /// ADR 0003: Feature code never saves, commits, begins or ends a transaction, or writes around the Unit of Work's
    /// tracked changes. The rule reads every method of the Module's assembly, its DbContext and Composition Root
    /// included, and leaves out only EF Core migrations. It rejects a call to:
    /// <list type="bullet">
    /// <item><c>DbContext.SaveChanges(Async)</c> or <c>DbContext.Database</c>, which reaches raw SQL, transactions and
    /// migrations;</item>
    /// <item>EF Core's bulk writes, <c>ExecuteUpdate(Async)</c> and <c>ExecuteDelete(Async)</c>, which write the database
    /// at once, outside the tracked changes the Unit of Work prepares;</item>
    /// <item>beginning, committing, rolling back or releasing a transaction or savepoint on an ADO.NET connection or
    /// transaction, or an EF Core transaction; a new <c>TransactionScope</c> or <c>CommittableTransaction</c>;</item>
    /// <item>the Kit's own steps: <c>IUnitOfWork</c>, the Unit of Work's DbContext (<c>EfUnitOfWorkDbContext</c>),
    /// <c>IUnitOfWorkOutbox</c>, <c>IReceipts</c>, <c>IIntegrationEventOutbox</c>, <c>IDelayedCommandOutbox</c>, <c>IIdempotencyStore</c>, <c>ISnapshotMappers</c>,
    /// <c>DomainEventDrain</c>, <c>IReadingStateGuard</c>, <c>TrackedState</c>, the query cache (<c>IQueryCache</c>), whose eviction follows the commit, opening a reading step (<c>Operation.OnlyReading</c>), and beginning an Operation
    /// (<c>BeginOperation</c>). The Gateway, the Web boundary and the Kit's delivery step begin Operations; so does an
    /// application of one Module that opens its own scopes, in its own assembly, which this rule would reject if that
    /// assembly were judged as a Module.</item>
    /// </list>
    /// It also rejects a string literal holding a transaction-control statement (<c>begin</c>, <c>start transaction</c>,
    /// <c>commit</c>, <c>end</c>, <c>abort</c>, <c>rollback</c>, <c>savepoint</c>, <c>release</c>, two-phase
    /// <c>prepare</c>/<c>commit prepared</c>), read as <see cref="HoldsTransactionControl"/> reads it. Not seen: a call
    /// made through reflection, <c>dynamic</c> or an expression tree compiled at run time; a call a library the Module
    /// calls makes for it; EF Core's transaction manager reached without <c>Database</c>
    /// (<c>GetService&lt;IDbContextTransactionManager&gt;()</c>); SQL text assembled at run time; a write on a
    /// connection outside the Unit of Work (the read connection from <c>IReadConnectionFactory.OpenAsync()</c>, or one
    /// the Module opens), which neither this rule nor the Dapper write rule sees when it names a table no aggregate maps.
    /// </summary>
    public static IEnumerable<string> SavesOrCommits(Assembly module) =>
        (
            from call in Il.CallsIn(module)
            where !IsMigration(call.Owner) && (IsSaveOrCommit(call.Callee) || IsBulkWrite(call.Callee) || IsKitStep(call.Callee))
            select $"{call.Owner.FullName} calls {call.Callee.DeclaringType!.Name}.{call.Callee.Name}."
        ).Concat(
            from literal in Il.LiteralsIn(module)
            where !IsMigration(literal.Owner) && HoldsTransactionControl(literal.Text)
            select $"{literal.Owner.FullName} runs the SQL statement \"{literal.Text}\"."
        ).Distinct();

    /// <summary>
    /// Whether <paramref name="sql"/> holds a transaction-control statement. Its comments, the bodies of its
    /// single-quoted strings and its dollar-quoted blocks are removed first; what is left is split at its semicolons, and
    /// it counts as SQL only when every statement starts with a SQL command word, so prose such as "Abort; retry?" is
    /// not. Not seen: transaction control inside a dollar-quoted block (a <c>DO</c> block or a procedure body that
    /// commits). A literal that reads as one such statement (a label reading "End" or "Release notes") is read as SQL.
    /// </summary>
    public static bool HoldsTransactionControl(string sql)
    {
        string[] statements = [.. Sql.Code(sql, keepDollarQuoted: false).Split(';').Select(statement => statement.Trim()).Where(statement => statement.Length > 0)];
        return statements.All(statement => SqlCommand().IsMatch(statement))
            && statements.Any(statement => TransactionStatement().IsMatch(statement));
    }

    /// <summary>
    /// ADR 0004 and 0006: no type from Wolverine or JasperFx, so never <c>IMessageBus</c>, <c>IMessageContext</c>,
    /// <c>IDbContextOutbox&lt;&gt;</c> or <c>EndpointFor</c>. Read from the assembly's type references, which hold
    /// every type its code, signatures and attributes name, including through a Kit method that exposes one. Not seen:
    /// a transport type reached by name at run time (reflection, <c>Type.GetType</c>, a service resolved by a
    /// <c>Type</c> built from a string) or held only as <c>object</c> or <c>dynamic</c>.
    /// </summary>
    public static IEnumerable<string> TransportTypesUsedBy(Assembly assembly)
    {
        using var stream = File.OpenRead(assembly.Location);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        List<string> found = [];
        foreach (var handle in metadata.TypeReferences)
        {
            var type = metadata.GetTypeReference(handle);
            var scope = type.ResolutionScope;
            while (scope.Kind == HandleKind.TypeReference)
            {
                scope = metadata.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;
            }

            if (scope.Kind == HandleKind.AssemblyReference)
            {
                var from = metadata.GetString(metadata.GetAssemblyReference((AssemblyReferenceHandle)scope).Name);
                if (IsTransport(from))
                {
                    found.Add($"{Name(assembly)} uses {metadata.GetString(type.Namespace)}.{metadata.GetString(type.Name)} from {from}.");
                }
            }
        }

        return found;
    }

    /// <summary>
    /// A Module's DbContext maps none of the message store's envelope tables (ADR 0004): no entity type from a Wolverine
    /// or JasperFx assembly, and no table named <c>wolverine_*</c>. Not seen: an envelope table mapped by an entity type
    /// of the Module's own under another table name.
    /// </summary>
    public static IEnumerable<string> EnvelopeStorageIn(string context, IModel model) =>
        from entityType in model.GetEntityTypes()
        where IsTransport(entityType.ClrType.Assembly.GetName().Name!)
            || (entityType.GetTableName()?.StartsWith("wolverine_", StringComparison.OrdinalIgnoreCase) ?? false)
        select $"{context} maps {entityType.DisplayName()} to {entityType.GetSchema()}.{entityType.GetTableName()}.";

    /// <summary>
    /// The tables a Module's aggregates map: every entity type of its model declared in the Module's assembly, so each
    /// aggregate root, its owned parts and its child entities, as <c>schema.table</c>.
    /// </summary>
    public static IEnumerable<string> AggregateTablesOf(Assembly module, IModel model) =>
        from entityType in model.GetEntityTypes()
        where entityType.ClrType.Assembly == module && entityType.GetTableName() is not null
        select $"{entityType.GetSchema() ?? model.GetDefaultSchema()}.{entityType.GetTableName()}".ToLowerInvariant();

    /// <summary>
    /// Story 45: SQL in a Module that writes (<c>insert into</c>, <c>update</c>, <c>delete from</c>, <c>merge into</c>,
    /// <c>truncate</c>) names no table an aggregate of any Module maps; a name without its schema is compared with every
    /// aggregate table of that name, and quotes, case and whitespace around the schema's dot do not matter. It reads the
    /// string literals of the Module's code outside EF Core migrations, so whatever SQL calls them (Dapper, ADO.NET,
    /// <c>ExecuteSqlRaw</c>), with their comments and the bodies of their single-quoted strings removed first; a
    /// dollar-quoted block is read as code, since a <c>DO</c> block that writes still writes. EF Core's LINQ bulk writes
    /// are rejected by <see cref="SavesOrCommits"/>. Not seen: SQL built at run time (an interpolated or concatenated
    /// table name, text read from a file or a resource); a three-part name (<c>database.schema.table</c>); a write
    /// through a view; writes inside stored procedures, functions or triggers the literal only calls; and a write on a
    /// connection outside the Unit of Work to a table no aggregate maps, which this rule allows by design.
    /// </summary>
    public static IEnumerable<string> WritesToAggregateTables(Assembly module, IReadOnlySet<string> aggregateTables) =>
        (
            from literal in Il.LiteralsIn(module)
            where !IsMigration(literal.Owner)
            from table in TablesWrittenBy(literal.Text)
            where aggregateTables.Contains(table) || aggregateTables.Any(mapped => mapped.EndsWith($".{table}", StringComparison.Ordinal))
            select $"{literal.Owner.FullName} writes the aggregate table {table} with SQL: \"{literal.Text.Trim()}\"."
        ).Distinct();

    /// <summary>The tables <paramref name="sql"/> writes, lower case, with their schema when it names one.</summary>
    public static IEnumerable<string> TablesWrittenBy(string sql) =>
        from match in SqlWrite().Matches(Sql.Code(sql, keepDollarQuoted: true))
        let table = Unquoted(match.Groups["table"].Value)
        select match.Groups["schema"].Success ? $"{Unquoted(match.Groups["schema"].Value)}.{table}" : table;

    private static string Unquoted(string name) => name.Replace("\"", "", StringComparison.Ordinal).ToLowerInvariant();

    /// <summary>
    /// Only the Kit's Domain Event drain clears an aggregate's Domain Events: a call to <c>ClearDomainEvents</c> on any
    /// aggregate type. Not seen: a call through reflection or <c>dynamic</c>.
    /// </summary>
    public static IEnumerable<string> ClearsDomainEvents(Assembly assembly) =>
        (
            from call in Il.CallsIn(assembly)
            where call.Callee.Name.EndsWith(nameof(IAggregateRoot.ClearDomainEvents), StringComparison.Ordinal)
                && call.Callee.DeclaringType!.IsAssignableTo(typeof(IAggregateRoot))
            select call.Owner.FullName!
        ).Distinct();

    /// <summary>
    /// ADR 0001: a Module's only public types are its Composition Root and the types its Host implements for it, listed
    /// by name; its Contracts project holds everything other Modules see. EF Core migrations and model snapshots are
    /// exempt by base type, since <c>dotnet ef migrations add</c> writes them public every time. Not seen:
    /// <c>InternalsVisibleTo</c>, which opens a Module's internal types to the assemblies it names.
    /// </summary>
    public static IEnumerable<string> PublicTypesOf(Assembly module, IReadOnlySet<Type> hostPorts) =>
        from type in module.GetExportedTypes()
        where !type.IsAssignableTo(typeof(IModuleDefinition)) && !IsMigration(type) && !hostPorts.Contains(type)
        select type.FullName!;

    private static bool IsSaveOrCommit(MethodBase callee)
    {
        var type = callee.DeclaringType!;
        return callee.Name switch
        {
            "SaveChanges" or "SaveChangesAsync" or "get_Database" => type.IsAssignableTo(typeof(DbContext)),
            "BeginTransaction" or "BeginTransactionAsync" or "BeginDbTransaction" or "BeginDbTransactionAsync" or "EnlistTransaction" =>
                type.IsAssignableTo(typeof(IDbConnection)) || type.IsAssignableTo(typeof(DbConnection)),
            "Commit" or "CommitAsync" or "Rollback" or "RollbackAsync" or "Save" or "SaveAsync" or "Release" or "ReleaseAsync" =>
                type.IsAssignableTo(typeof(IDbTransaction)) || type.IsAssignableTo(typeof(IDbContextTransaction)),
            ".ctor" => type == typeof(TransactionScope) || type == typeof(CommittableTransaction),
            _ => false,
        };
    }

    private static bool IsBulkWrite(MethodBase callee) =>
        callee.Name is "ExecuteUpdate" or "ExecuteUpdateAsync" or "ExecuteDelete" or "ExecuteDeleteAsync"
        && (callee.DeclaringType!.Namespace?.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ?? false);

    // The seams the Kit's own steps call. IOutbox is a marker with nothing to call, and Operation's other
    // public members only read it, so neither is listed.
    private static readonly Type[] KitSteps =
    [
        typeof(IUnitOfWork),
        typeof(IUnitOfWorkOutbox),
        typeof(IReceipts),
        typeof(IIntegrationEventOutbox),
        typeof(IDelayedCommandOutbox),
        typeof(IIdempotencyStore),
        typeof(ISnapshotMappers),
        typeof(DomainEventDrain),
        typeof(IReadingStateGuard),
        typeof(TrackedState),
        typeof(IQueryCache),
    ];

    private static bool IsKitStep(MethodBase callee) =>
        KitSteps.Any(step => callee.DeclaringType!.IsAssignableTo(step))
        || callee.DeclaringType == typeof(EfUnitOfWorkDbContext)
        || (callee.DeclaringType == typeof(OperationContextServiceProviderExtensions) && callee.Name == "BeginOperation")
        || (callee.DeclaringType == typeof(Operation) && callee.Name == nameof(Operation.OnlyReading));

    /// <summary>
    /// Spec #33 and ADR 0002: applications register no pipeline steps, and an application's own implementation of a
    /// contract the Kit fills for its pipeline would be one by another name, whatever registers it. Each contract is
    /// public only so another Kit package can fill or call it:
    /// <list type="bullet">
    /// <item><c>IDispatcher</c>: the fixed pipeline itself; Cqrs fills it and Observability decorates it, so an application's
    /// decorator or replacement would wrap every Operation in steps of its own;</item>
    /// <item><c>IGatewayProcessor&lt;TModule&gt;</c>: the Gateway that begins each Operation sent from outside its Module;
    /// Modularity fills it;</item>
    /// <item><c>IUnitOfWork</c>: the dispatcher runs every outermost command in it; the EF Unit of Work fills it (ADR 0003);</item>
    /// <item><c>IValidationStep&lt;T&gt;</c>: the dispatcher's validation step; Validation fills it from the validators
    /// Features write;</item>
    /// <item><c>IReadingStateGuard</c>: the check of tracked state across every step that only reads; the EF Unit of Work
    /// fills it;</item>
    /// <item><c>IUniqueConstraintReader</c>: the save step's reading of a refused save's unique constraint, which the EF Unit
    /// of Work turns into the Module's Error; Persistence.Postgres fills it;</item>
    /// <item><c>IIdempotencyStore</c> and <c>IQueryCache</c>: the pipeline's two optional slots; Persistence.EFCore and
    /// Caching fill them;</item>
    /// <item><c>IUnitOfWorkOutbox</c>: the Unit of Work's Outbox write and send; Messaging.Wolverine.EFCore fills it and
    /// marks it with <c>IOutbox</c>, so an application's own marker would pass the check that a Host which
    /// publishes has an Outbox while nothing stores what it publishes;</item>
    /// <item><c>IIntegrationEventOutbox</c> and <c>IDelayedCommandOutbox</c>: the Integration Events and delayed commands
    /// of an Operation wait in them for its commit; Messaging.Wolverine.EFCore fills them;</item>
    /// <item><c>ISnapshotMappers</c>: the Outbox builds the Snapshots of changed aggregates through it; Messaging.Wolverine
    /// fills it;</item>
    /// <item><c>IReceipts</c>: the delivery step records a Receipt in the receiving Unit of Work; Messaging.Wolverine.EFCore
    /// fills it.</item>
    /// </list>
    /// Unlike the steps <see cref="SavesOrCommits"/> names, which Feature code must not call, the list holds the dispatcher
    /// and the Gateway, which Feature code calls, the marker and the validation step, which have nothing to call, and no
    /// sealed class, which nothing can implement. The rule reads every type of the assembly, nested and compiler-generated
    /// ones included, and rejects a class or struct that implements one, naming the assembly's file; a Kit package
    /// implementing one is correct. Not seen: an implementation made at run time, such as a <c>DispatchProxy</c>.
    /// </summary>
    public static IEnumerable<string> ImplementsPipelineContracts(Assembly assembly) =>
        from type in assembly.GetTypes()
        where !type.IsInterface
        from contract in PipelineContracts
        where contract.IsGenericTypeDefinition
            ? type.GetInterfaces().Any(implemented => implemented.IsGenericType && implemented.GetGenericTypeDefinition() == contract)
            : type.IsAssignableTo(contract)
        select $"{type.FullName} implements {contract.Name.Split('`')[0]}, a pipeline contract only the Kit implements ({Path.GetFileName(assembly.Location)}).";

    private static readonly Type[] PipelineContracts =
    [
        typeof(IDispatcher),
        typeof(IGatewayProcessor<>),
        typeof(IUnitOfWork),
        typeof(IValidationStep<>),
        typeof(IReadingStateGuard),
        typeof(IUniqueConstraintReader),
        typeof(IIdempotencyStore),
        typeof(IQueryCache),
        typeof(IUnitOfWorkOutbox),
        typeof(IOutbox),
        typeof(IIntegrationEventOutbox),
        typeof(IDelayedCommandOutbox),
        typeof(ISnapshotMappers),
        typeof(IReceipts),
    ];

    /// <summary>
    /// The tables of <paramref name="model"/> that hold one tenant's rows per row, as <c>schema.table</c>: every table an
    /// <c>ITenantScoped</c> entity type maps, with its tenant column, and every table of a part a navigation holds in a
    /// tenant-scoped aggregate, a part of a part too, with its keys to the tables that hold it, as the Kit's query filter
    /// finds them (#153): a key to the part's own type, or to the table it shares with its holder, holds nothing. A type
    /// derived from a part and mapped to a table of its own (TPT) is a part of its base type's table, which its primary
    /// key joins; a derived type that shares its base type's table adds none. Each further table any of these entity types
    /// splits into (<c>SplitToTable</c>) is a part of the entity type's own table, which its primary key joins.
    /// </summary>
    public static IEnumerable<KeyValuePair<string, TenantTable>> TenantTablesOf(IModel model) =>
        from entityType in model.GetEntityTypes()
        where entityType.GetTableName() is not null && (entityType.BaseType is null || TableOf(entityType.BaseType) != TableOf(entityType))
        let tenantScoped = entityType.ClrType.IsAssignableTo(typeof(ITenantScoped))
        let holders = tenantScoped ? [] : HolderKeysOf(entityType)
        where tenantScoped || holders.Count > 0
        let primaryKey = entityType.FindPrimaryKey()!.Properties
        from table in entityType
            .GetMappingFragments(StoreObjectType.Table)
            .Select(fragment =>
                KeyValuePair.Create(
                    $"{fragment.StoreObject.Schema ?? model.GetDefaultSchema()}.{fragment.StoreObject.Name}".ToLowerInvariant(),
                    new TenantTable(null, [new HolderKey(TableOf(entityType), ColumnsIn(fragment.StoreObject, primaryKey, entityType, primaryKey)!)])
                )
            )
            .Prepend(
                KeyValuePair.Create(
                    TableOf(entityType),
                    new TenantTable(
                        tenantScoped ? entityType.FindProperty(nameof(ITenantScoped.Tenant))!.GetColumnName().ToLowerInvariant() : null,
                        holders
                    )
                )
            )
        select table;

    /// <summary>
    /// A table that holds one tenant's rows per row: an <c>ITenantScoped</c> entity's, with its <paramref name="Tenant"/>
    /// column, or a part's, with no tenant column and its keys to the tables that hold it, <paramref name="Holders"/>.
    /// </summary>
    internal sealed record TenantTable(string? Tenant, IReadOnlyList<HolderKey> Holders);

    /// <summary>A part's key to the <paramref name="Holder"/> table: each of its columns with the holder's column it names.</summary>
    internal sealed record HolderKey(string Holder, IReadOnlyList<(string Column, string HolderColumn)> Columns);

    private static string TableOf(IReadOnlyEntityType entityType) =>
        $"{entityType.GetSchema() ?? entityType.Model.GetDefaultSchema()}.{entityType.GetTableName()}".ToLowerInvariant();

    // A part's keys to its holders whose columns its own table holds, those its base type declares included; a type
    // mapped to a table of its own below a part (TPT) keeps them in its base type's table, which its primary key joins.
    private static List<HolderKey> HolderKeysOf(IReadOnlyEntityType part)
    {
        List<HolderKey> keys =
        [
            .. from foreignKey in part.GetForeignKeys()
            where foreignKey.PrincipalToDependent is not null
                && foreignKey.PrincipalEntityType.GetRootType() != part.GetRootType()
                && TableOf(foreignKey.PrincipalEntityType) != TableOf(part)
                && InTenantScopedAggregate(foreignKey.PrincipalEntityType, [])
            let columns = ColumnsIn(StoreOf(part), foreignKey.Properties, foreignKey.PrincipalEntityType, foreignKey.PrincipalKey.Properties)
            where columns is not null
            select new HolderKey(TableOf(foreignKey.PrincipalEntityType), columns),
        ];
        if (
            part.BaseType is { } baseType
            && baseType.GetTableName() is not null
            && TableOf(baseType) != TableOf(part)
            && HolderKeysOf(baseType).Count > 0
            && ColumnsIn(StoreOf(part), part.FindPrimaryKey()!.Properties, baseType, part.FindPrimaryKey()!.Properties) is { } toBase
        )
        {
            keys.Add(new HolderKey(TableOf(baseType), toBase));
        }

        return keys;
    }

    private static StoreObjectIdentifier StoreOf(IReadOnlyEntityType entityType) => StoreObjectIdentifier.Table(entityType.GetTableName()!, entityType.GetSchema());

    // Each of `columns` in `partTable` with its holder column in `holder`'s table; null when either table lacks one.
    private static List<(string Column, string HolderColumn)>? ColumnsIn(
        StoreObjectIdentifier partTable,
        IReadOnlyList<IReadOnlyProperty> columns,
        IReadOnlyEntityType holder,
        IReadOnlyList<IReadOnlyProperty> holderColumns
    )
    {
        var holderTable = StoreOf(holder);
        var pairs = columns.Zip(holderColumns, (column, holderColumn) => (column.GetColumnName(partTable), holderColumn.GetColumnName(holderTable))).ToList();
        return pairs.All(pair => pair is (not null, not null))
            ? [.. pairs.Select(pair => (pair.Item1!.ToLowerInvariant(), pair.Item2!.ToLowerInvariant()))]
            : null;
    }

    // The holder is tenant-scoped, or a part held, through a navigation, by one that is: PouryaKitModelConvention's walk.
    private static bool InTenantScopedAggregate(IReadOnlyEntityType holder, HashSet<IReadOnlyEntityType> seen) =>
        holder.ClrType.IsAssignableTo(typeof(ITenantScoped))
        || (
            !holder.ClrType.IsAssignableTo(typeof(IAggregateRoot))
            && seen.Add(holder)
            && holder.GetForeignKeys().Any(foreignKey => foreignKey.PrincipalToDependent is not null && InTenantScopedAggregate(foreignKey.PrincipalEntityType, seen))
        );

    // The tenant-scoped tables `table` is held under: itself when it is one, else those its holders are held under.
    private static IEnumerable<string> RootsOf(string table, IReadOnlyDictionary<string, TenantTable> tenantTables) =>
        tenantTables[table].Tenant is not null
            ? [table]
            : tenantTables[table].Holders.Where(key => tenantTables.ContainsKey(key.Holder)).SelectMany(key => RootsOf(key.Holder, tenantTables)).Distinct();

    /// <summary>
    /// Spec #115, Multi-tenancy: Dapper runs exactly the SQL a Feature writes, so in a tenant-scoped Module a string literal
    /// that reads a tenant-scoped table keeps to one grammar over its tokens (<see cref="Sql.Tokens"/>), one statement of
    /// <c>SELECT list FROM item ((JOIN | INNER JOIN | LEFT JOIN) item ON condition | , item)* WHERE conjunct (AND conjunct)*
    /// [ORDER BY list] [LIMIT @p | n] [OFFSET @p | n]</c>, and every other shape is rejected. An item is
    /// <c>[schema.]table [[AS] alias]</c> with nothing after it; no <c>SELECT</c>, <c>TABLE</c> or set operator appears
    /// anywhere else; a condition or a conjunct has no <c>OR</c> or <c>CASE</c> outside parentheses (a <c>BETWEEN</c> takes
    /// the <c>AND</c> after it); each item of an <c>ITenantScoped</c> table has a conjunct that is exactly
    /// <c>alias.tenant = @Tenant</c>, or, for an item with no alias, qualified by its table with or without its schema, or
    /// unqualified when the <c>FROM</c> has that one item; and each item of a part's table, which has no tenant column, is
    /// joined to an item of its holder's table that is held in turn, up to a tenant-scoped item so held: by a conjunct
    /// <c>part.column = holder.column</c>, both qualified, for every column of one of its keys to that holder, in the
    /// <c>ON</c> condition of the part, or of the holder when the holder comes later by no <c>LEFT JOIN</c>, which would
    /// keep every row of the part. The tokeniser rejects a block comment, a <c>$</c>, a backslash and a character it
    /// does not classify. A literal reads a tenant-scoped table when its text, comments and quotes included, names one and
    /// holds a SQL command word, <c>from</c> or <c>join</c> (so a route or a permission named after a table is no SQL); a
    /// <c>U&amp;</c> identifier may name any of them. A literal that writes a tenant-scoped table is left to
    /// <see cref="WritesToAggregateTables"/>, which rejects it, since an aggregate maps every tenant-scoped table. The
    /// literals are read outside EF Core migrations, so a fragment such as <c>from catalog.products</c>, concatenated at
    /// run time, is rejected as no <c>SELECT</c>. Not seen: SQL read from a file or a resource, or assembled from fragments
    /// of which none holds both a SQL word and the table's name; a read through a view or a function; and whether
    /// <c>@Tenant</c> holds the Operation's tenant, which the Gateway tests show.
    /// </summary>
    public static IEnumerable<string> ReadsBeyondTheTenant(Assembly module, IReadOnlyDictionary<string, TenantTable> tenantTables) =>
        (
            from literal in Il.LiteralsIn(module)
            where !IsMigration(literal.Owner)
            let fault = TenantReadFault(literal.Text, tenantTables)
            where fault is not null
            select $"{literal.Owner.FullName} reads a tenant-scoped table with SQL that {fault}: \"{literal.Text.Trim()}\"."
        ).Distinct();

    /// <summary>The tenant-scoped tables <paramref name="sql"/> reads, as <see cref="ReadsBeyondTheTenant"/> finds them.</summary>
    public static IEnumerable<string> TenantTablesReadBy(string sql, IReadOnlyDictionary<string, TenantTable> tenantTables)
    {
        if (!SqlWord().IsMatch(sql))
        {
            return [];
        }

        if (UnicodeEscape().IsMatch(sql))
        {
            return tenantTables.Keys;
        }

        HashSet<string> words = [.. AnyWord().Matches(sql).Select(word => word.Value.ToLowerInvariant())];
        return tenantTables.Keys.Where(table => words.Contains(table[(table.IndexOf('.') + 1)..]));
    }

    /// <summary>
    /// Why <paramref name="sql"/>, which reads a tenant-scoped table, breaks the shape <see cref="ReadsBeyondTheTenant"/>
    /// holds it to; null when it keeps to it or reads no such table.
    /// </summary>
    public static string? TenantReadFault(string sql, IReadOnlyDictionary<string, TenantTable> tenantTables)
    {
        if (!TenantTablesReadBy(sql, tenantTables).Any() || TablesWrittenBy(sql).Any(table => TenantTableNamed(table, tenantTables) is not null))
        {
            return null;
        }

        var (tokens, fault) = Sql.Tokens(sql);
        return fault is not null ? $"{fault}, {Unread}" : new TenantSelect(tokens, tenantTables).Fault();
    }

    private const string Unread = "a shape the rule does not read";

    // The tenant-scoped table `name` is, `schema.table` or a table of that name in any schema; null when it is none.
    private static string? TenantTableNamed(string name, IReadOnlyDictionary<string, TenantTable> tenantTables) =>
        name.Contains('.')
            ? tenantTables.ContainsKey(name) ? name : null
            : tenantTables.Keys.FirstOrDefault(table => table.EndsWith($".{name}", StringComparison.Ordinal));

    // The tokens of one literal read as the SELECT TenantReadFault admits, the rule's grammar written out as code.
    private sealed class TenantSelect
    {
        private static readonly SqlToken End = new(SqlTokenKind.Punctuation, "");

        // Words that are no alias: what may follow a FROM item, and what starts none.
        private static readonly HashSet<string> NotAliases =
        [
            "as", "on", "using", "where", "join", "inner", "left", "right", "full", "cross", "natural", "lateral", "only",
            "tablesample", "group", "having", "window", "order", "limit", "offset", "fetch", "for", "union", "intersect",
            "except", "select", "from",
        ];

        // Words that end a join condition outside parentheses, beside a comma.
        private static readonly HashSet<string> ConditionEnds =
        [
            "join", "inner", "left", "right", "full", "cross", "natural", "where", "group", "having", "window", "order", "limit",
            "offset", "fetch", "for",
        ];

        // Words that end a WHERE or an ORDER BY list outside parentheses.
        private static readonly HashSet<string> WhereEnds = ["group", "having", "window", "order", "limit", "offset", "fetch", "for"];

        private readonly IReadOnlyDictionary<string, TenantTable> _tenantTables;
        private readonly SqlToken[] _tokens;
        private readonly int[] _depths;
        private readonly List<FromItem> _items = [];
        private int _at;

        public TenantSelect(IReadOnlyList<SqlToken> tokens, IReadOnlyDictionary<string, TenantTable> tenantTables)
        {
            _tenantTables = tenantTables;
            var last = tokens.Count;
            while (last > 0 && tokens[last - 1].Is(';'))
            {
                last--;
            }

            _tokens = [.. tokens.Take(last)];
            _depths = new int[_tokens.Length];
            var depth = 0;
            for (var at = 0; at < _tokens.Length; at++)
            {
                depth -= _tokens[at].Is(')') ? 1 : 0;
                _depths[at] = depth;
                depth += _tokens[at].Is('(') ? 1 : 0;
            }
        }

        private SqlToken Next => Peek(0);

        private bool AtEnd => _at >= _tokens.Length;

        // Whether the next token stands outside every parenthesis.
        private bool Outside => !AtEnd && _depths[_at] == 0;

        private SqlToken Peek(int ahead) => _at + ahead < _tokens.Length ? _tokens[_at + ahead] : End;

        public string? Fault()
        {
            if (_tokens.Any(token => token.Is(';')))
            {
                return "is not one statement";
            }

            if (_tokens is [] || !_tokens[0].Is("select"))
            {
                return "is not a SELECT";
            }

            if (_tokens.FirstOrDefault(token => token.Is("union") || token.Is("intersect") || token.Is("except")) is { Text: not null } setOperator)
            {
                return $"has the set operator {setOperator.Name}";
            }

            if (_tokens.Skip(1).Any(token => token.Is("select") || token.Is("table")))
            {
                return "has a subquery";
            }

            if (_depths.Any(depth => depth < 0) || _tokens.Count(token => token.Is('(')) != _tokens.Count(token => token.Is(')')))
            {
                return $"has parentheses that do not pair, {Unread}";
            }

            _at = 1;
            while (!AtEnd && !(Outside && Next.Is("from")))
            {
                _at++;
            }

            if (AtEnd)
            {
                return "has no FROM";
            }

            _at++;
            return From() ?? Where();
        }

        // The FROM clause, up to and past its WHERE.
        private string? From()
        {
            var joined = false;
            var left = false;
            while (true)
            {
                if (Item() is { } itemFault)
                {
                    return itemFault;
                }

                var item = _items[^1];
                var after = $"the FROM item {item}";
                if (joined)
                {
                    if (!Next.Is("on"))
                    {
                        return $"has {Shown(Next)} after {after}, {Unread}";
                    }

                    _at++;
                    var start = _at;
                    while (!AtEnd && !(Outside && (Next.Is(',') || (Next.Kind == SqlTokenKind.Word && ConditionEnds.Contains(Next.Name)))))
                    {
                        _at++;
                    }

                    after = $"the join condition of {item}";
                    if (Unsplittable(start, _at, after) is { } conditionFault)
                    {
                        return conditionFault;
                    }

                    _items[^1] = item with { Left = left, On = [.. Conjuncts(start, _at)] };
                }

                if (AtEnd || (Next.Kind == SqlTokenKind.Word && WhereEnds.Contains(Next.Name)))
                {
                    return "has no WHERE";
                }

                if (Next.Is("where"))
                {
                    _at++;
                    return null;
                }

                if (Next.Is(','))
                {
                    joined = false;
                    _at++;
                }
                else if (Next.Is("join"))
                {
                    joined = true;
                    left = false;
                    _at++;
                }
                else if ((Next.Is("inner") || Next.Is("left")) && Peek(1).Is("join"))
                {
                    joined = true;
                    left = Next.Is("left");
                    _at += 2;
                }
                else
                {
                    return $"has {Shown(Next.Is("inner") || Next.Is("left") ? Peek(1) : Next)} after {after}, {Unread}";
                }
            }
        }

        // One FROM item: `[schema.]table [[AS] alias]`.
        private string? Item()
        {
            if (Next.Is('('))
            {
                return "has a FROM item in parentheses";
            }

            if (!Next.IsName || (Next.Kind == SqlTokenKind.Word && NotAliases.Contains(Next.Name)))
            {
                return $"has {Shown(Next)} where a FROM item starts, {Unread}";
            }

            var table = Next.Name;
            _at++;
            if (Next.Is('.') && Peek(1).IsName)
            {
                table = $"{table}.{Peek(1).Name}";
                _at += 2;
            }

            string? alias = null;
            if (Next.Is("as"))
            {
                if (!Peek(1).IsName)
                {
                    return $"has {Shown(Peek(1))} after AS in the FROM item {table}, {Unread}";
                }

                alias = Peek(1).Name;
                _at += 2;
            }
            else if (Next.Kind == SqlTokenKind.Quoted || (Next.Kind == SqlTokenKind.Word && !NotAliases.Contains(Next.Name)))
            {
                alias = Next.Name;
                _at++;
            }

            _items.Add(new FromItem(table, alias));
            return null;
        }

        // The WHERE and what may follow it, then each tenant-scoped item held to the tenant.
        private string? Where()
        {
            var start = _at;
            while (!AtEnd && !(Outside && Next.Kind == SqlTokenKind.Word && WhereEnds.Contains(Next.Name)))
            {
                _at++;
            }

            if (Unsplittable(start, _at, "its WHERE") is { } whereFault)
            {
                return whereFault;
            }

            var conjuncts = Conjuncts(start, _at).Select(TenantColumnOf).OfType<Column>().ToArray();
            if (Next.Is("order") && Peek(1).Is("by"))
            {
                _at += 2;
                while (!AtEnd && !(Outside && Next.Kind == SqlTokenKind.Word && WhereEnds.Contains(Next.Name)))
                {
                    _at++;
                }
            }

            if (Next.Is("limit") && Peek(1).Kind is SqlTokenKind.Parameter or SqlTokenKind.Number)
            {
                _at += 2;
            }

            if (Next.Is("offset") && Peek(1).Kind is SqlTokenKind.Parameter or SqlTokenKind.Number)
            {
                _at += 2;
            }

            if (!AtEnd)
            {
                return $"has {Shown(Next)} after its WHERE, {Unread}";
            }

            // Each tenant-scoped item is held by its conjunct; then each part joined to a held item of its holder, until
            // no more parts are held, so a part of a part follows its holder.
            var held = new bool[_items.Count];
            for (var at = 0; at < _items.Count; at++)
            {
                held[at] = TableAt(at) is { } table
                    && _tenantTables[table].Tenant is { } column
                    && conjuncts.Any(conjunct =>
                        conjunct.Name == column && (conjunct.Qualifier is { } qualifier ? NamesOf(at).Contains(qualifier) : _items.Count == 1)
                    );
            }

            for (var found = true; found;)
            {
                found = false;
                for (var part = 0; part < _items.Count; part++)
                {
                    if (
                        !held[part]
                        && TableAt(part) is { } table
                        && _tenantTables[table].Tenant is null
                        && Enumerable.Range(0, _items.Count).Any(holder => held[holder] && _tenantTables[table].Holders.Any(key => Joins(part, holder, key)))
                    )
                    {
                        held[part] = found = true;
                    }
                }
            }

            for (var at = 0; at < _items.Count; at++)
            {
                if (!held[at] && TableAt(at) is { } table)
                {
                    var item = $"{table}{(_items[at].Alias is { } alias ? $" {alias}" : "")}";
                    return _tenantTables[table].Tenant is null
                        ? $"does not join the part {item} on its key to its holder up to {string.Join(" or ", RootsOf(table, _tenantTables))} held to @Tenant"
                        : $"does not hold {item} to @Tenant with a top-level conjunct of its WHERE";
                }
            }

            return null;
        }

        // The tenant-scoped table of the FROM item at `at`; null when it is none.
        private string? TableAt(int at) => TenantTableNamed(_items[at].Table, _tenantTables);

        // The names a column of the tenant-scoped item at `at` may be qualified by: its alias, or else its table with or
        // without its schema.
        private string[] NamesOf(int at) =>
            _items[at] is { Alias: { } alias } ? [alias] : [_items[at].Table[(_items[at].Table.IndexOf('.') + 1)..], TableAt(at)!];

        // Whether the part at `part` is joined to the item at `holder` on every column of `key`, in the ON of the later of
        // the two: the part's, or the holder's when it is no LEFT JOIN, which would keep every row of the part.
        private bool Joins(int part, int holder, HolderKey key)
        {
            var later = _items[Math.Max(part, holder)];
            return TableAt(holder) == key.Holder
                && (holder < part || !later.Left)
                && key.Columns.All(column =>
                    later.On.Select(Equality.Of).Any(equality =>
                        equality is not null
                        && (
                            (Names(equality.Left, part, column.Column) && Names(equality.Right, holder, column.HolderColumn))
                            || (Names(equality.Left, holder, column.HolderColumn) && Names(equality.Right, part, column.Column))
                        )
                    )
                );
        }

        // Whether `column` is the column `name` of the item at `at`, qualified by one of its names.
        private bool Names(Column column, int at, string name) =>
            column is { Qualifier: { } qualifier } && column.Name == name && NamesOf(at).Contains(qualifier);

        // Why the tokens from `start` to `end` cannot be split into conjuncts: an OR or a CASE outside parentheses.
        private string? Unsplittable(int start, int end, string clause)
        {
            for (var at = start; at < end; at++)
            {
                if (_depths[at] == 0 && _tokens[at].Is("or"))
                {
                    return $"has an OR at the top level of {clause}";
                }

                if (_depths[at] == 0 && _tokens[at].Is("case"))
                {
                    return $"has a CASE at the top level of {clause}";
                }
            }

            return null;
        }

        // The conjuncts of the WHERE from `start` to `end`: its tokens split at each AND outside parentheses but the one a
        // BETWEEN takes.
        private IEnumerable<SqlToken[]> Conjuncts(int start, int end)
        {
            var between = false;
            for (var at = start; at < end; at++)
            {
                if (_depths[at] != 0)
                {
                    continue;
                }

                if (_tokens[at].Is("between"))
                {
                    between = true;
                }
                else if (_tokens[at].Is("and") && between)
                {
                    between = false;
                }
                else if (_tokens[at].Is("and"))
                {
                    yield return _tokens[start..at];
                    start = at + 1;
                }
            }

            yield return _tokens[start..end];
        }

        private static string Shown(SqlToken token) =>
            token.Kind switch
            {
                SqlTokenKind.Quoted => $"\"{token.Text}\"",
                SqlTokenKind.String => $"'{token.Text}'",
                _ => token.Text.Length == 0 ? "nothing" : token.Text,
            };

        // A FROM item; a joined one with whether it is a LEFT JOIN and the conjuncts of its ON condition.
        private sealed record FromItem(string Table, string? Alias)
        {
            public bool Left { get; init; }

            public SqlToken[][] On { get; init; } = [];

            public override string ToString() => Alias is null ? Table : $"{Table} {Alias}";
        }

        // The column of a conjunct that is exactly `column = @Tenant`.
        private static Column? TenantColumnOf(SqlToken[] conjunct) =>
            conjunct is [.., var equals, { Kind: SqlTokenKind.Parameter } tenant]
            && equals.Is('=')
            && tenant.Text.Equals("@Tenant", StringComparison.OrdinalIgnoreCase)
                ? Column.Of(conjunct[..^2])
                : null;

        // A column `[qualifier.]name`, the qualifier a table with or without its schema, or an alias.
        private sealed record Column(string? Qualifier, string Name)
        {
            public static Column? Of(SqlToken[] tokens) =>
                tokens switch
                {
                    [{ IsName: true } column] => new(null, column.Name),
                    [{ IsName: true } table, var dot, { IsName: true } column] when dot.Is('.') => new(table.Name, column.Name),
                    [{ IsName: true } schema, var dot, { IsName: true } table, var tableDot, { IsName: true } column] when dot.Is('.') && tableDot.Is('.') =>
                        new($"{schema.Name}.{table.Name}", column.Name),
                    _ => null,
                };
        }

        // A conjunct that is exactly `column = column`.
        private sealed record Equality(Column Left, Column Right)
        {
            public static Equality? Of(SqlToken[] conjunct) =>
                Array.FindIndex(conjunct, token => token.Is('=')) is var at and > 0
                && Column.Of(conjunct[..at]) is { } left
                && Column.Of(conjunct[(at + 1)..]) is { } right
                    ? new(left, right)
                    : null;
        }
    }

    private static bool IsMigration(Type type) => type.IsAssignableTo(typeof(Migration)) || type.IsAssignableTo(typeof(ModelSnapshot));

    private static bool IsTransport(string assemblyName) =>
        assemblyName.StartsWith("Wolverine", StringComparison.Ordinal) || assemblyName.StartsWith("JasperFx", StringComparison.Ordinal);

    private static string Name(Assembly assembly) => assembly.GetName().Name!;

    // One statement of a literal, trimmed: PostgreSQL's transaction control, with the optional words each takes.
    [GeneratedRegex(
        """
        ^(?:
            (?:begin(?:\s+(?:transaction|work))?|start\s+transaction)
                (?:\s*,?\s*(?:isolation\s+level\s+\w+(?:\s+\w+)?|read\s+(?:write|only)|(?:not\s+)?deferrable))*
          | (?:commit|end|abort|rollback)(?:\s+(?:transaction|work))?(?:\s+and\s+(?:no\s+)?chain)?
          | rollback(?:\s+(?:transaction|work))?\s+to(?:\s+savepoint)?\s+\w+
          | savepoint\s+\w+
          | release(?:\s+savepoint)?\s+\w+
          | (?:prepare\s+transaction|commit\s+prepared|rollback\s+prepared)\s+'[^']*'
        )$
        """,
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace
    )]
    private static partial Regex TransactionStatement();

    // PostgreSQL's command words.
    private const string CommandWords =
        "abort|alter|analyze|begin|call|checkpoint|close|cluster|comment|commit|copy|create|deallocate|declare|delete|discard|do|drop|end|execute|explain|fetch|grant|import|insert|listen|load|lock|merge|move|notify|prepare|reassign|refresh|reindex|release|reset|revoke|rollback|savepoint|security|select|set|show|start|table|truncate|unlisten|update|vacuum|values|with";

    // A statement that starts with a PostgreSQL command word.
    [GeneratedRegex($@"^(?:{CommandWords})\b", RegexOptions.IgnoreCase)]
    private static partial Regex SqlCommand();

    [GeneratedRegex(
        @"\b(?:insert\s+into|(?<!\b(?:for|on)\s+)update(?!\s+set\b)|delete\s+from|merge\s+into|truncate(?:\s+table)?)\s+(?:only\s+)?(?:(?<schema>""?[a-z_][\w$]*""?)\s*\.\s*)?(?<table>""?[a-z_][\w$]*""?)",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex SqlWrite();

    // A word that SQL, or a fragment of it, holds: a command word, `from` or `join`.
    [GeneratedRegex($@"\b(?:{CommandWords}|from|join)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SqlWord();

    // A Unicode-escaped identifier, which may spell any name without its letters.
    [GeneratedRegex(@"\bu&""", RegexOptions.IgnoreCase)]
    private static partial Regex UnicodeEscape();

    [GeneratedRegex("[A-Za-z0-9_]+")]
    private static partial Regex AnyWord();
}
