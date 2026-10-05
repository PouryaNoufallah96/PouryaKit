namespace PouryaKit.Cqrs;

/// <summary>
/// The person or system on whose authority an Operation runs. There is no anonymous Actor: work that no person caused
/// names the system that runs it.
/// </summary>
public sealed record Actor
{
    private Actor(string id, ActorKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
        Kind = kind;
    }

    /// <summary>The person's identifier, or the system's name.</summary>
    public string Id { get; }

    public ActorKind Kind { get; }

    /// <summary>A person, identified as the trust boundary that authenticated them identifies them.</summary>
    public static Actor Person(string id) => new(id, ActorKind.Person);

    /// <summary>
    /// A named system identity, for independent work no person caused, such as a scheduled job (G.6). Work that
    /// continues an Operation keeps that Operation's Actor instead.
    /// </summary>
    public static Actor System(string name) => new(name, ActorKind.System);
}

public enum ActorKind
{
    Person,
    System,
}
