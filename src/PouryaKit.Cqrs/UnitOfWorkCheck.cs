namespace PouryaKit.Cqrs;

// A command whose handler is registered in a Host that registers no Unit of Work would fail at its first dispatch, which
// container validation cannot see. In a Host of Modules the one Unit of Work routes to each Module's, whose own is
// checked when the Module is composed.
internal sealed class UnitOfWorkCheck : ICompositionCheck
{
    public IEnumerable<string> Check(HostComposition composition, IServiceProvider services)
    {
        var unkeyed = composition.Registrations.Where(descriptor => !descriptor.IsKeyedService).ToList();
        var command = unkeyed
            .Select(descriptor => OperationTypes.HandledBy(descriptor.ServiceType))
            .FirstOrDefault(operation => operation is not null && OperationTypes.IsCommand(operation));
        return command is null || unkeyed.Any(descriptor => descriptor.ServiceType == typeof(IUnitOfWork))
            ? []
            : [
                $"The Host handles the command {command.FullName} but registers no Unit of Work, so the command would fail when it runs. Register the Unit of Work of a persistence package, for example with UsePostgres.",
            ];
    }
}
