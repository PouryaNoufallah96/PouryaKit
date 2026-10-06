namespace Rmq.Contracts;

/// <summary>Catalog's Integration Event, version 1. Its wire name is declared, never derived from this CLR name.</summary>
public sealed record ProductCreatedV1(Guid ProductId, string Name)
{
    public const string WireName = "catalog.product-created.v1";
}
