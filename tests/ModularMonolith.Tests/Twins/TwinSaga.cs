using ModularMonolith.Tests.Sagas;
using PouryaKit.Messaging.Wolverine.Sagas;

namespace ModularMonolith.Tests.Twins;

/// <summary>A saga of the same simple name as <see cref="ModularMonolith.Tests.TwinSaga"/>, in another namespace.</summary>
public sealed class TwinSaga : PouryaKitSaga
{
    /// <summary>Which of the two twins this is.</summary>
    public string Twin { get; set; } = "";

    /// <summary>Starts the saga.</summary>
    public static TwinSaga Start(SagaInput<TwinSaga, ReferenceOpenedV1> opened)
    {
        ArgumentNullException.ThrowIfNull(opened);
        return new() { Twin = "other" };
    }
}
