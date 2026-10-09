using System.Reflection;
using System.Runtime.CompilerServices;
using PouryaKit.Cqrs;

namespace PouryaKit.ArchitectureTests;

// Ticket #57 and ADR 0007: only Kit code that restores a context from material the Kit wrote may mark it a trusted
// continuation. Cqrs opens its internals to exactly those packages, and they use nothing internal of Cqrs but that
// restore. No negative fixture: an assembly outside the list cannot compile a call to an internal member, so the list
// itself is the rule; the second test judges what the list lets through.
public sealed class TrustedRestoreTests
{
    private static readonly string[] Opened = ["PouryaKit.Messaging.Wolverine", "PouryaKit.Web"];

    private static readonly Assembly Cqrs = typeof(OperationContext).Assembly;

    [Fact]
    public void Cqrs_opens_its_internals_to_kit_delivery_and_web_only()
    {
        Assert.Equal(
            Opened,
            Cqrs.GetCustomAttributes<InternalsVisibleToAttribute>().Select(opened => opened.AssemblyName).Order(StringComparer.Ordinal)
        );
    }

    [Fact]
    public void The_packages_cqrs_opens_to_use_no_internal_member_of_it_but_the_trusted_restore()
    {
        var internalCalls = (
            from package in Sample.KitPackages
            where Opened.Contains(package.GetName().Name)
            from call in Il.CallsIn(package)
            where call.Callee.DeclaringType?.Assembly == Cqrs && IsInternal(call.Callee)
            select (Package: package.GetName().Name, Callee: $"{call.Callee.DeclaringType!.Name}.{call.Callee.Name}", call.Owner)
        ).ToList();

        Assert.Empty(
            from call in internalCalls
            where call.Callee != $"{nameof(OperationContext)}.RestoreTrusted"
            select $"{call.Owner.FullName} in {call.Package} calls {call.Callee}, an internal member of PouryaKit.Cqrs."
        );
        // The rule reads the calls it allows: delivery restores the context it runs with, and Web the context a trusted
        // peer's request continues.
        Assert.Contains(internalCalls, call => call.Package == "PouryaKit.Messaging.Wolverine");
        Assert.Contains(internalCalls, call => call.Package == "PouryaKit.Web");
    }

    // Internal to Cqrs: an internal member, or any member of a type only Cqrs can name. A constructed generic type is
    // judged by its definition, so CommandHandler<SomeInternalCommand> stays public.
    private static bool IsInternal(MethodBase member)
    {
        var type = member.DeclaringType!;
        var definition = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
        return member.IsAssembly || member.IsFamilyAndAssembly || !definition.IsVisible;
    }
}
