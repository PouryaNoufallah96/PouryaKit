namespace PouryaKit.Caching;

/// <summary>
/// Declares the cache tags a command invalidates: every entry of a <see cref="ICacheableQuery"/> with one of them, for
/// every Actor, is evicted once the command's Operation commits. The tags of the outermost command and of every command
/// nested in its Unit of Work are evicted together; a rolled-back Operation evicts nothing. Every command that changes a
/// cached resource declares its tag. The tags are read from the command alone, before its handler runs, so a command
/// that finds what it changes by a criterion declares a tag every entry it may change carries. Declare it on a
/// <c>Command</c> or <c>Command&lt;T&gt;</c>.
/// </summary>
public interface IInvalidatesCache
{
    IReadOnlyCollection<string> InvalidatedCacheTags { get; }
}
