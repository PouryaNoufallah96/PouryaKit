namespace PouryaKit.Modularity;

/// <summary>
/// A Module's Composition Root: the one place that declares everything the Module needs and offers. Implement it as a
/// public sealed class in the Module's assembly; everything else in the Module stays internal.
/// </summary>
public interface IModuleDefinition
{
    /// <summary>Registers the Module's data, Features and services on <paramref name="module"/>.</summary>
    void Configure(ModuleBuilder module);
}
