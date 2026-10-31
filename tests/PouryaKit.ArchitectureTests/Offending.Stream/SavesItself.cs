using Marten;
using PouryaKit.Cqrs;
using PouryaKit.Persistence;
using PouryaKit.Persistence.Marten;

namespace Offending.Stream;

/// <summary>A document an offending Stream Module stores itself.</summary>
public sealed record Draft(Guid Id);

/// <summary>Feature code that saves through Marten's document session, past its Unit of Work.</summary>
public sealed class SavesItself(IDocumentSession session)
{
    public async Task KeepAsync(Draft draft, CancellationToken cancellationToken)
    {
        session.Store(draft);
        await session.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>Feature code that runs the Marten Unit of Work's own steps.</summary>
public sealed class RunsMartenSteps(IUnitOfWork unitOfWork, IStreamOutbox outbox)
{
    public async Task WriteAsync(CancellationToken cancellationToken)
    {
        await MartenUnitOfWorkTransaction.ExecuteAsync(
            unitOfWork,
            IdempotencyStatements.Reserve,
            IdempotencyStatements.Table,
            [],
            cancellationToken
        );
        await outbox.WriteAsync(null!, [], cancellationToken);
    }
}
