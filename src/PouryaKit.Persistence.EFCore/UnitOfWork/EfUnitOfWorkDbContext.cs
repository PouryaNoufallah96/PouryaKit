using Microsoft.EntityFrameworkCore;
using PouryaKit.Cqrs;

namespace PouryaKit.Persistence.EFCore;

/// <summary>
/// Reaches the DbContext an EF Core Unit of Work saves and commits, for a Kit package that writes in the Unit of Work's
/// transaction, such as a Notification Handler's Receipt. A static accessor rather than a member of
/// <see cref="IUnitOfWork"/>, so a repository's completion never offers it; Feature code changes data through its
/// repositories, and a structure test refuses a Module that calls it.
/// </summary>
public static class EfUnitOfWorkDbContext
{
    /// <summary>
    /// The DbContext <paramref name="unitOfWork"/> saves and commits, the Module's own in a modular Host; null for any
    /// other Unit of Work.
    /// </summary>
    public static DbContext? Of(IUnitOfWork unitOfWork) => (unitOfWork as EfUnitOfWork)?.DbContext;
}
