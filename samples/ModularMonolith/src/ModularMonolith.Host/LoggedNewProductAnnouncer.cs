using Catalog.Products.Events.Notifications;

namespace ModularMonolith.Host;

/// <summary>Announces a new product in the Host's log, where a real application would send it to its customers.</summary>
public sealed partial class LoggedNewProductAnnouncer(ILogger<LoggedNewProductAnnouncer> logger)
    : INewProductAnnouncer
{
    public Task AnnounceAsync(
        NewProductAnnouncement announcement,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(announcement);
        LogAnnounced(logger, announcement.Name, announcement.ProductId);
        return Task.CompletedTask;
    }

    [LoggerMessage(
        EventName = "Announced",
        Level = LogLevel.Information,
        Message = "New in the catalog: {Name} ({ProductId})"
    )]
    private static partial void LogAnnounced(ILogger logger, string name, Guid productId);
}
