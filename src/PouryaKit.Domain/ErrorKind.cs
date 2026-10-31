namespace PouryaKit.Domain;

/// <summary>The semantic kind of an <see cref="Error"/>. A transport maps it to its own codes; the domain never does.</summary>
public enum ErrorKind
{
    /// <summary>The Operation's input breaks a rule.</summary>
    Validation,

    /// <summary>Something the Operation needs does not exist.</summary>
    NotFound,

    /// <summary>
    /// The Operation conflicts with the current state, such as a unique value already in use or a stale version.
    /// </summary>
    Conflict,

    /// <summary>The Actor may not run the Operation.</summary>
    Forbidden,

    /// <summary>A failure no other kind describes.</summary>
    Unexpected,
}
