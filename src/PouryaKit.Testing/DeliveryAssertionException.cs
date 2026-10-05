namespace PouryaKit.Testing;

/// <summary>A delivery assertion of <see cref="HostFixture"/> failed; xUnit reports it as an assertion failure.</summary>
public sealed class DeliveryAssertionException(string message) : Exception(message), IAssertionException;

// xUnit reports an exception as an assertion failure when it implements an interface of this name, in any namespace.
internal interface IAssertionException;
