using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Security;

System.Type[] types = [typeof(IActorPermissions)];

// The packed Security's optional password hashing, registered and called as an application would.
var services = new ServiceCollection().AddPouryaKitPasswordHashing(options => options.Iterations = 700_000);
var hasher = (IPasswordHasher)services.Single(descriptor => descriptor.ServiceType == typeof(IPasswordHasher)).ImplementationInstance!;
var stored = hasher.Hash("package-consumer");
Console.WriteLine(types.Length + (hasher.Verify("package-consumer", stored) == PasswordVerification.Succeeded ? 1 : 0));
