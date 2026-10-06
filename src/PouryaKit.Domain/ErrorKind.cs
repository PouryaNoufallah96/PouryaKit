namespace PouryaKit.Domain;

/// <summary>The semantic kind of an <see cref="Error"/>. A transport maps it to its own codes; the domain never does.</summary>
public enum ErrorKind
{
    Validation,
    NotFound,
    Conflict,
    Forbidden,
    Unexpected,
}
