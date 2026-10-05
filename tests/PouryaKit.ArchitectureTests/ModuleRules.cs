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
using PouryaKit.Modularity;

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
    /// implementation fails, direct or through other assemblies of the application. A Module that resolves another's
    /// public services at run time by name, without referencing it, is not seen.
    /// </summary>
    public static IEnumerable<string> ReferencesToOtherModules(Assembly module, IEnumerable<Assembly> modules) =>
        from reached in Sample.ProjectReferencesOf(module)
        where reached != module && modules.Contains(reached)
        select $"{Name(module)} references {Name(reached)}, the implementation of another Module; reach it through {Name(reached)}.Contracts.";

    /// <summary>Contracts are plain records other Modules compile against: nothing of the Kit, and no Module.</summary>
    public static IEnumerable<string> ReferencesOfContracts(Assembly contracts, IEnumerable<Assembly> modules) =>
        from reference in contracts.GetReferencedAssemblies()
        where reference.Name!.StartsWith("PouryaKit", StringComparison.Ordinal) || modules.Any(module => Name(module) == reference.Name)
        select $"{Name(contracts)} references {reference.Name}; Contracts reference nothing from the Kit and no Module.";

    /// <summary>
    /// ADR 0003: Feature code never saves, commits, begins or ends a transaction, or touches <c>DbContext.Database</c>.
    /// The rule reads every method of the Module's assembly, its DbContext and Composition Root included, and leaves out
    /// only EF Core migrations. It sees calls the Module's own code makes and literal SQL transaction statements
    /// (<c>commit</c>, <c>begin</c>, <c>savepoint</c>, ...); a save reached through a library the Module calls, or
    /// through reflection, is not seen.
    /// </summary>
    public static IEnumerable<string> SavesOrCommits(Assembly module) =>
        (
            from call in Il.CallsIn(module)
            where !IsMigration(call.Owner) && IsSaveOrCommit(call.Callee)
            select $"{call.Owner.FullName} calls {call.Callee.DeclaringType!.Name}.{call.Callee.Name}."
        ).Concat(
            from literal in Il.LiteralsIn(module)
            where !IsMigration(literal.Owner) && TransactionStatement().IsMatch(literal.Text)
            select $"{literal.Owner.FullName} runs the SQL statement \"{literal.Text}\"."
        ).Distinct();

    /// <summary>
    /// ADR 0004 and 0006: no type from Wolverine or JasperFx, so never <c>IMessageBus</c>, <c>IMessageContext</c>,
    /// <c>IDbContextOutbox&lt;&gt;</c> or <c>EndpointFor</c>. Read from the assembly's type references, which hold
    /// every type its code, signatures and attributes name, including through a Kit method that exposes one.
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

    /// <summary>A Module's DbContext maps none of the message store's envelope tables (ADR 0004).</summary>
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
    /// aggregate table of that name. It reads the string literals of the Module's code outside EF Core migrations, so
    /// whatever SQL calls them (Dapper, ADO.NET, <c>ExecuteSqlRaw</c>). SQL built at run time (an interpolated or
    /// concatenated table name, text read from a file or a resource) and writes inside stored procedures, functions or
    /// triggers are not seen.
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
        from match in SqlWrite().Matches(sql)
        select match.Groups["table"].Value.Replace("\"", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();

    /// <summary>Only the Kit's Domain Event drain clears an aggregate's Domain Events.</summary>
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
    /// exempt by base type, since <c>dotnet ef migrations add</c> writes them public every time.
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
            _ => type.IsAssignableTo(typeof(IUnitOfWork)),
        };
    }

    private static bool IsMigration(Type type) => type.IsAssignableTo(typeof(Migration)) || type.IsAssignableTo(typeof(ModelSnapshot));

    private static bool IsTransport(string assemblyName) =>
        assemblyName.StartsWith("Wolverine", StringComparison.Ordinal) || assemblyName.StartsWith("JasperFx", StringComparison.Ordinal);

    private static string Name(Assembly assembly) => assembly.GetName().Name!;

    [GeneratedRegex(
        @"^\s*(begin(\s+(transaction|work))?|start\s+transaction|commit(\s+(transaction|work))?|rollback(\s+(transaction|work))?(\s+to(\s+savepoint)?\s+\w+)?|savepoint\s+\w+|release(\s+savepoint)?\s+\w+)\s*;?\s*$",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex TransactionStatement();

    [GeneratedRegex(
        @"\b(?:insert\s+into|(?<!\b(?:for|on)\s+)update(?!\s+set\b)|delete\s+from|merge\s+into|truncate(?:\s+table)?)\s+(?:only\s+)?(?<table>""?[a-z_][\w$]*""?(?:\s*\.\s*""?[a-z_][\w$]*""?)?)",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex SqlWrite();
}
