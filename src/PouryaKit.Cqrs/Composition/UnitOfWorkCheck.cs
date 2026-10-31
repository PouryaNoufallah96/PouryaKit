using System.Reflection;

namespace PouryaKit.Cqrs;

// A Feature's command whose handler is registered in a Host that registers no Unit of Work would fail at its first
// dispatch, which container validation cannot see. Only the commands of the Host's Features count: those whose type, or
// whose registered handler, is in an assembly its registrations scanned (HostComposition), since a scanned handler may
// handle a command from an assembly nothing scans, such as a Contracts assembly. A Kit package's own commands, such as
// the delivery commands UsePouryaKitWolverine registers, belong to no Feature and run only for a declaration, so a Host
// that declares nothing never runs them; registering them per declaration instead would make a Module's builder record
// a Kit command as that Module's. In a Host of Modules the one Unit of Work routes to each Module's, whose own is checked
// when the Module is composed.
internal sealed class UnitOfWorkCheck : ICompositionCheck
{
    public IEnumerable<string> Check(HostComposition composition, IServiceProvider services)
    {
        HashSet<Assembly> features =
        [
            .. composition.FeatureAssembliesOf(null),
            .. composition.Modules.SelectMany(composition.FeatureAssembliesOf),
        ];
        var unkeyed = composition
            .Registrations.Where(descriptor => !descriptor.IsKeyedService)
            .ToList();
        var command = unkeyed
            .Select(descriptor =>
                (
                    Operation: OperationTypes.HandledBy(descriptor.ServiceType),
                    Handler: descriptor.ImplementationType
                )
            )
            .FirstOrDefault(handled =>
                handled.Operation is not null
                && OperationTypes.IsCommand(handled.Operation)
                && (
                    features.Contains(handled.Operation.Assembly)
                    || (handled.Handler is not null && features.Contains(handled.Handler.Assembly))
                )
            )
            .Operation;
        return
            command is null
            || unkeyed.Any(descriptor => descriptor.ServiceType == typeof(IUnitOfWork))
            ? []
            :
            [
                $"The Host handles the command {command.FullName} but registers no Unit of Work, so the command would fail when it runs. Register the Unit of Work of a persistence package, for example with UsePouryaKitPostgres.",
            ];
    }
}
