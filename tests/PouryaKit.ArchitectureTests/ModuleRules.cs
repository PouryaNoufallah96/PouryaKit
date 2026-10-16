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
    /// The tables of <paramref name="model"/> that hold one tenant's rows per row, as <c>schema.table</c>, each with its
    /// tenant column: every table an <c>ITenantScoped</c> entity type maps.
    /// </summary>
    public static IEnumerable<KeyValuePair<string, string>> TenantTablesOf(IModel model) =>
        from entityType in model.GetEntityTypes()
        where entityType.ClrType.IsAssignableTo(typeof(ITenantScoped)) && entityType.GetTableName() is not null
        select KeyValuePair.Create(
            $"{entityType.GetSchema() ?? model.GetDefaultSchema()}.{entityType.GetTableName()}".ToLowerInvariant(),
            entityType.FindProperty(nameof(ITenantScoped.Tenant))!.GetColumnName().ToLowerInvariant()
        );

    /// <summary>
    /// Spec #115, Multi-tenancy: Dapper runs exactly the SQL a Feature writes, so in a tenant-scoped Module a string literal
    /// that reads a tenant-scoped table is one <c>SELECT</c> with no set operator and no subquery, whose <c>WHERE</c> has no
    /// top-level <c>OR</c> and holds every tenant-scoped table it reads to the tenant with a top-level conjunct of its own:
    /// <c>p.tenant = @Tenant</c> by the table's alias, or by its name when it has none, and <c>tenant = @Tenant</c>
    /// unqualified only when its <c>FROM</c> clause reads that one table. Every other shape of such a literal is rejected,
    /// so the predicate inside an <c>OR</c>, in one branch of a <c>UNION</c> or for one table of a join is not enough, and
    /// so is a shape the rule does not parse: a <c>CASE</c> at the top level of the <c>WHERE</c>, a dollar-quoted block, or
    /// a <c>FROM</c> item in parentheses. Keywords inside double-quoted identifiers are no keywords. A
    /// literal reads a table it names after <c>from</c>, <c>join</c> or a comma, with or without its schema, compared as
    /// <see cref="WritesToAggregateTables"/> compares names; the table of a <c>delete from</c> is a write, which that rule
    /// judges. The literals are read as that rule reads them, outside EF Core migrations, so a fragment that names such a
    /// table and is concatenated at run time is rejected as no <c>SELECT</c>. Not seen: SQL read from a file or a resource;
    /// a read through a view or a function; and whether <c>@Tenant</c> holds the Operation's tenant, which the Gateway
    /// tests show.
    /// </summary>
    public static IEnumerable<string> ReadsBeyondTheTenant(Assembly module, IReadOnlyDictionary<string, string> tenantTables) =>
        (
            from literal in Il.LiteralsIn(module)
            where !IsMigration(literal.Owner)
            let fault = TenantReadFault(literal.Text, tenantTables)
            where fault is not null
            select $"{literal.Owner.FullName} reads a tenant-scoped table with SQL that {fault}: \"{literal.Text.Trim()}\"."
        ).Distinct();

    /// <summary>The tenant-scoped tables <paramref name="sql"/> reads, as <see cref="ReadsBeyondTheTenant"/> finds them.</summary>
    public static IEnumerable<string> TenantTablesReadBy(string sql, IReadOnlyDictionary<string, string> tenantTables) =>
        from read in TableReads(Sql.Code(sql, keepDollarQuoted: true))
        let table = TenantTableOf(read, tenantTables)
        where table is not null
        select table;

    /// <summary>
    /// Why <paramref name="sql"/>, which reads a tenant-scoped table, breaks the shape <see cref="ReadsBeyondTheTenant"/>
    /// holds it to; null when it keeps to it or reads no such table.
    /// </summary>
    public static string? TenantReadFault(string sql, IReadOnlyDictionary<string, string> tenantTables)
    {
        var code = Sql.Code(sql, keepDollarQuoted: true);
        TableRead[] reads = [.. TableReads(code).Where(read => TenantTableOf(read, tenantTables) is not null)];
        if (reads is [])
        {
            return null;
        }

        // The rule judges only what it can read: a shape it cannot parse is rejected, not read further. Its keywords are
        // looked for with the bodies of double-quoted identifiers blanked, so a column named "limit" ends no clause.
        if (Sql.Code(sql, keepDollarQuoted: false) != code)
        {
            return "holds a dollar-quoted block";
        }

        var top = Sql.TopLevel(code);
        var scan = Sql.WithoutIdentifiers(top);
        if (code.Split(';').Count(statement => !string.IsNullOrWhiteSpace(statement)) != 1)
        {
            return "is not one statement";
        }

        if (!SelectStatement().IsMatch(scan))
        {
            return "is not a SELECT";
        }

        var words = Sql.WithoutIdentifiers(code);
        if (SetOperator().Match(words) is { Success: true } setOperator)
        {
            return $"has the set operator {setOperator.Value.ToLowerInvariant()}";
        }

        if (SelectWord().Count(words) > 1)
        {
            return "has a subquery";
        }

        if (WhereClause().Match(scan) is not { Success: true } where)
        {
            return "has no WHERE";
        }

        var from = FromWord().Match(scan);
        var fromClause = from.Success && from.Index < where.Index ? scan[from.Index..where.Index] : "";
        if (ParenthesizedFromItem().IsMatch(fromClause))
        {
            return "has a FROM item in parentheses";
        }

        var whereSpan = where.Groups["where"];
        if (OrWord().IsMatch(whereSpan.Value))
        {
            return "has an OR at the top level of its WHERE";
        }

        if (CaseWord().IsMatch(whereSpan.Value))
        {
            return "has a CASE at the top level of its WHERE";
        }

        var fromItems = fromClause.Length > 0 ? TableReads(top[from.Index..where.Index]).Count() : 0;
        TenantConjunct[] conjuncts =
        [
            .. Conjuncts(top.Substring(whereSpan.Index, whereSpan.Length), whereSpan.Value).Select(TenantConjunct.Of).OfType<TenantConjunct>(),
        ];
        foreach (var read in reads)
        {
            var table = TenantTableOf(read, tenantTables)!;
            string[] names = read.Alias is { } alias ? [alias] : [read.Table, table];
            if (!conjuncts.Any(conjunct =>
                    conjunct.Column == tenantTables[table] && (conjunct.Qualifier is { } qualifier ? names.Contains(qualifier) : fromItems == 1)
                ))
            {
                return $"does not hold {table}{(read.Alias is null ? "" : $" {read.Alias}")} to @Tenant with a top-level conjunct of its WHERE";
            }
        }

        return null;
    }

    private sealed record TableRead(string? Schema, string Table, string? Alias);

    private static IEnumerable<TableRead> TableReads(string code) =>
        from match in SqlRead().Matches(code)
        select new TableRead(
            match.Groups["schema"].Success ? Unquoted(match.Groups["schema"].Value) : null,
            Unquoted(match.Groups["table"].Value),
            match.Groups["alias"].Success ? Unquoted(match.Groups["alias"].Value) : null
        );

    private static string? TenantTableOf(TableRead read, IReadOnlyDictionary<string, string> tenantTables) =>
        read.Schema is { } schema
            ? tenantTables.ContainsKey($"{schema}.{read.Table}") ? $"{schema}.{read.Table}" : null
            : tenantTables.Keys.FirstOrDefault(table => table.EndsWith($".{read.Table}", StringComparison.Ordinal));

    // The top-level conjuncts of a WHERE with no top-level OR or CASE: its text split at each AND but the one a BETWEEN
    // takes, the words found in `scan`, the same text with its double-quoted identifiers blanked.
    private static IEnumerable<string> Conjuncts(string where, string scan)
    {
        var start = 0;
        var between = false;
        foreach (Match word in AndOrBetween().Matches(scan))
        {
            if (word.Value.Equals("between", StringComparison.OrdinalIgnoreCase))
            {
                between = true;
            }
            else if (between)
            {
                between = false;
            }
            else
            {
                yield return where[start..word.Index];
                start = word.Index + word.Length;
            }
        }

        yield return where[start..];
    }

    // A conjunct that is exactly `[qualifier.]column = @Tenant`, the qualifier with or without its schema.
    private sealed record TenantConjunct(string? Qualifier, string Column)
    {
        public static TenantConjunct? Of(string conjunct) =>
            TenantPredicate().Match(conjunct) is { Success: true } match
                ? new TenantConjunct(
                    match.Groups["qualifier"].Success
                        ? Unquoted(string.Concat(match.Groups["qualifier"].Value.Where(character => !char.IsWhiteSpace(character))))
                        : null,
                    Unquoted(match.Groups["column"].Value)
                )
                : null;
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

    // A statement that starts with a PostgreSQL command word.
    [GeneratedRegex(
        @"^(?:abort|alter|analyze|begin|call|checkpoint|close|cluster|comment|commit|copy|create|deallocate|declare|delete|discard|do|drop|end|execute|explain|fetch|grant|import|insert|listen|load|lock|merge|move|notify|prepare|reassign|refresh|reindex|release|reset|revoke|rollback|savepoint|security|select|set|show|start|table|truncate|unlisten|update|vacuum|values|with)\b",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex SqlCommand();

    [GeneratedRegex(
        @"\b(?:insert\s+into|(?<!\b(?:for|on)\s+)update(?!\s+set\b)|delete\s+from|merge\s+into|truncate(?:\s+table)?)\s+(?:only\s+)?(?:(?<schema>""?[a-z_][\w$]*""?)\s*\.\s*)?(?<table>""?[a-z_][\w$]*""?)",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex SqlWrite();

    // A table a literal reads: named after `from` (not the table of a `delete from`), `join` or a comma, with its alias.
    [GeneratedRegex(
        """
        (?:(?<!\bdelete\s+)\bfrom\b|\bjoin\b|,)[\s(]*(?:only\s+)?
        (?:(?<schema>"?[a-z_][\w$]*"?)\s*\.\s*)?(?<table>"?[a-z_][\w$]*"?)
        (?:\s+(?:as\s+)?(?!(?:on|using|join|inner|left|right|full|cross|natural|lateral|tablesample|where|group|having|window|order|limit|offset|fetch|for|union|intersect|except|from)\b)(?<alias>"?[a-z_][\w$]*"?))?
        """,
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace
    )]
    private static partial Regex SqlRead();

    [GeneratedRegex(@"^\s*select\b", RegexOptions.IgnoreCase)]
    private static partial Regex SelectStatement();

    [GeneratedRegex(@"\bselect\b", RegexOptions.IgnoreCase)]
    private static partial Regex SelectWord();

    [GeneratedRegex(@"\b(?:union|intersect|except)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SetOperator();

    [GeneratedRegex(@"\bfrom\b", RegexOptions.IgnoreCase)]
    private static partial Regex FromWord();

    // A top-level WHERE, up to the clause that follows it.
    [GeneratedRegex(
        @"\bwhere\b(?<where>.*?)(?=\b(?:group\s+by|having|window|order\s+by|limit|offset|fetch|for)\b|$)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline
    )]
    private static partial Regex WhereClause();

    [GeneratedRegex(@"\bor\b", RegexOptions.IgnoreCase)]
    private static partial Regex OrWord();

    [GeneratedRegex(@"\bcase\b", RegexOptions.IgnoreCase)]
    private static partial Regex CaseWord();

    // A FROM item that opens a parenthesis: a parenthesized join, relation or subquery, whose relations the rule does not read.
    [GeneratedRegex(@"(?:\bfrom\b|\bjoin\b|,)\s*(?:(?:only|lateral)\s+)?\(", RegexOptions.IgnoreCase)]
    private static partial Regex ParenthesizedFromItem();

    [GeneratedRegex(@"\b(?:and|between)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AndOrBetween();

    [GeneratedRegex(
        """^\s*(?:(?<qualifier>"?[a-z_][\w$]*"?(?:\s*\.\s*"?[a-z_][\w$]*"?)?)\s*\.\s*)?(?<column>"?[a-z_][\w$]*"?)\s*=\s*@tenant\s*$""",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex TenantPredicate();
}
