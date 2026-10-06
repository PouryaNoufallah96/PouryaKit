namespace Catalog.Products.Events.Notifications;

/// <summary>Tells the world outside the Catalog that a product is new; the Host decides how.</summary>
public interface INewProductAnnouncer
{
    Task AnnounceAsync(NewProductAnnouncement announcement, CancellationToken cancellationToken);
}

public sealed record NewProductAnnouncement(Guid ProductId, string Name);
