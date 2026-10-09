using Microsoft.EntityFrameworkCore;
using PouryaKit.Cqrs;

namespace PouryaKit.Persistence.EFCore;

public static class EfUnitOfWorkExtensions
{
    extension(IUnitOfWork unitOfWork)
    {
        /// <summary>
        /// The DbContext an EF Core Unit of Work saves and commits, the Module's own in a modular Host; null for any other
        /// Unit of Work. It is for a Kit package that writes in the Unit of Work's transaction, such as a Notification
        /// Handler's Receipt; Feature code changes data through its repositories.
        /// </summary>
        public DbContext? DbContext => (unitOfWork as EfUnitOfWork)?.DbContext;
    }
}
