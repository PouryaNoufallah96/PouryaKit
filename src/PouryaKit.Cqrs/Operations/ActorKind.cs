namespace PouryaKit.Cqrs;

/// <summary>The two kinds of Actor: a person, or a named system doing work no person caused.</summary>
public enum ActorKind
{
    /// <summary>A person, on whose authority the Operation runs.</summary>
    Person,

    /// <summary>A named system, doing work no person caused.</summary>
    System,
}
