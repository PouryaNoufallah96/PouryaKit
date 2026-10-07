using System.Reflection;
using System.Text.Json;
using Offending;
using PouryaKit.Domain;

namespace PouryaKit.ArchitectureTests;

// Spec #33, user story 8: one JSON representation, owned by Domain. Web and Messaging.Wolverine apply Domain's
// UsePouryaKitJson to the options their framework hands them, Cqrs to the options it records an idempotent
// command's response with (#42), and Caching to the options it stores a cached query's answer with (#43); no other code
// sets JSON options up its own way.
public sealed class SharedJsonTests
{
    private static readonly Assembly Domain = typeof(TypedId).Assembly;

    [Fact]
    public void Web_messaging_wolverine_cqrs_and_caching_apply_domains_json_options_and_nothing_else_sets_up_its_own()
    {
        Assert.Empty(Sample.KitPackages.Where(package => package != Domain).Concat(Sample.Modules).SelectMany(OwnJsonSetups));
        Assert.Equal(
            [
                "PouryaKit.Caching.HybridQueryCache",
                "PouryaKit.Cqrs.Dispatcher",
                "PouryaKit.Messaging.Wolverine.WolverineHostApplicationBuilderExtensions",
                "PouryaKit.Web.WebServiceCollectionExtensions",
            ],
            Sample.KitPackages.SelectMany(Il.CallsIn).Where(AppliesKitJson).Select(call => call.Owner.FullName).Distinct().Order(StringComparer.Ordinal)
        );
        // Domain itself is where the options are set up, and the rule sees it.
        Assert.Contains("PouryaKit.Domain.JsonSerializerOptionsExtensions creates TypedIdJsonConverterFactory.", OwnJsonSetups(Domain));
    }

    [Fact]
    public void Code_that_sets_up_json_options_of_its_own_is_rejected()
    {
        Assert.Equal(
            [
                "Offending.OwnJson calls JsonSerializerOptions.set_PropertyNamingPolicy.",
                "Offending.OwnJson creates JsonSerializerOptions without applying UsePouryaKitJson.",
                "Offending.OwnJson creates TypedIdJsonConverterFactory.",
            ],
            OwnJsonSetups(typeof(OffendingModule).Assembly).Order(StringComparer.Ordinal)
        );
    }

    // A method may create options only to apply UsePouryaKitJson to them; nothing else sets an option or adds the
    // typed-ID converter itself.
    private static IEnumerable<string> OwnJsonSetups(Assembly assembly)
    {
        var calls = Il.CallsIn(assembly).ToList();
        var applying = calls.Where(AppliesKitJson).Select(call => call.Caller).ToHashSet();
        return calls
            .Select(call =>
                (call.Callee, call.Callee.DeclaringType) switch
                {
                    (ConstructorInfo, var type) when type == typeof(TypedIdJsonConverterFactory) =>
                        $"{call.Owner.FullName} creates TypedIdJsonConverterFactory.",
                    (ConstructorInfo, var type) when type == typeof(JsonSerializerOptions) && !applying.Contains(call.Caller) =>
                        $"{call.Owner.FullName} creates JsonSerializerOptions without applying UsePouryaKitJson.",
                    (MethodInfo { Name: var name }, var type) when type == typeof(JsonSerializerOptions) && name.StartsWith("set_", StringComparison.Ordinal) =>
                        $"{call.Owner.FullName} calls JsonSerializerOptions.{name}.",
                    _ => null,
                }
            )
            .OfType<string>()
            .Distinct();
    }

    private static bool AppliesKitJson(Il.Call call) =>
        call.Callee.DeclaringType == typeof(JsonSerializerOptionsExtensions)
        && call.Callee.Name == nameof(JsonSerializerOptionsExtensions.UsePouryaKitJson);
}
