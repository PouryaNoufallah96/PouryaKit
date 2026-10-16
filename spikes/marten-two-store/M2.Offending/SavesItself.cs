using Marten;

namespace M2.Offending;

internal sealed record Draft(Guid Id);

internal sealed class SavesItself(IDocumentSession session)
{
    public async Task KeepAsync(Draft draft, CancellationToken cancellationToken)
    {
        session.Store(draft);
        await session.SaveChangesAsync(cancellationToken);
    }
}
