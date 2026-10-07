using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;

namespace PouryaKit.Messaging.Wolverine;

/// <summary>
/// A command type a Module may schedule with <see cref="IDelayedCommands"/>. Its lower-cased <see cref="Name"/> is its
/// durable queue and half of its Receipts' key; <see cref="Module"/> is the Module that schedules it and that its
/// deliveries run in, null in an application without Modules.
/// </summary>
internal sealed record DelayedCommand(string Name, Type Command, Type? Module);

/// <summary>
/// Every delayed command one Host declares. It lives in the Host's service collection, as the subscriptions do, so
/// every Module's declaration adds to the same one and two Hosts in one process never share it.
/// </summary>
internal sealed class DelayedCommandDeclarations
{
    private readonly Dictionary<Type, DelayedCommand> _byCommand = [];

    public IEnumerable<DelayedCommand> All => _byCommand.Values;

    public static DelayedCommandDeclarations In(IServiceCollection services)
    {
        if (services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(DelayedCommandDeclarations)) is { } existing)
        {
            return (DelayedCommandDeclarations)existing.ImplementationInstance!;
        }

        var declarations = new DelayedCommandDeclarations();
        services.AddSingleton(declarations);
        return declarations;
    }

    public DelayedCommand? Of(Type command) => _byCommand.GetValueOrDefault(command);

    /// <summary>The declaration named <paramref name="name"/>, ignoring case; null when none has it.</summary>
    public DelayedCommand? Named(string name) =>
        _byCommand.Values.FirstOrDefault(declared => string.Equals(declared.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Adds the declaration, or throws naming each problem: the command type is abstract or generic, the name is missing,
    /// invalid, a queue Wolverine keeps for itself or another declaration's, ignoring case, or the type is already
    /// declared under another name or for another Module. Declaring the same again adds nothing.
    /// </summary>
    public void Add(string name, Type command, Type? module)
    {
        List<string> problems = [];
        if (command.IsAbstract || command.ContainsGenericParameters || !command.IsAssignableTo(typeof(Command)))
        {
            problems.Add($"{command.FullName} is not a concrete command without a response; only such a command can run later.");
        }

        if (Subscriptions.Check(name, $"{command.FullName}'s delayed command name", isQueue: true, problems))
        {
            var declared = new DelayedCommand(name.ToLowerInvariant(), command, module);
            if (_byCommand.TryGetValue(command, out var existing))
            {
                if (existing != declared)
                {
                    problems.Add(
                        $"{command.FullName} is declared as the delayed command {existing.Name} of {existing.Module?.Name ?? "the application"} and as {declared.Name} of {module?.Name ?? "the application"}; declare it once."
                    );
                }
            }
            else if (_byCommand.Values.FirstOrDefault(other => other.Name == declared.Name) is { } named)
            {
                problems.Add($"{command.FullName} and {named.Command.FullName} have the delayed command name {declared.Name}, ignoring case; give one another name.");
            }

            if (problems.Count == 0)
            {
                _byCommand.TryAdd(command, declared);
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, problems));
        }
    }
}
