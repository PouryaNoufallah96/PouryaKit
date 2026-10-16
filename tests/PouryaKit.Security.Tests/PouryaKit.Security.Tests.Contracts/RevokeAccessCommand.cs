using PouryaKit.Cqrs;

namespace PouryaKit.Security.Tests.Contracts;

/// <summary>A command another assembly declares, with a permission; the test Module's handler handles it.</summary>
[RequiresPermission(Permission)]
public sealed record RevokeAccessCommand : Command
{
    /// <summary>The permission it declares.</summary>
    public const string Permission = "tests.revoke-access";
}
