using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Security;

System.Type[] types = [typeof(IActorPermissions)];

// The packed Security's optional password hashing, registered, configured and called as an application would.
var services = new ServiceCollection().AddPouryaKitPasswordHashing(options =>
    options.Iterations = 700_000
);
services.Configure<PouryaKitPasswordHashingOptions>(options => options.SaltSize = 32);
var hasher = services.BuildServiceProvider().GetRequiredService<IPasswordHasher>();
var stored = hasher.Hash("package-consumer");
Console.WriteLine(
    types.Length
        + (hasher.Verify("package-consumer", stored) == PasswordVerification.Succeeded ? 1 : 0)
);
