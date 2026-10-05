namespace PouryaKit.Modularity;

/// <summary>The Module whose Gateway opened the current scope; null in a scope no Gateway opened.</summary>
internal sealed class ModuleTag
{
    public Type? Module { get; set; }
}
