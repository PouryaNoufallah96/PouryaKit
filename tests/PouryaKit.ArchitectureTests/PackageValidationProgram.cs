namespace PouryaKit.ArchitectureTests;

internal static class PackageValidationProgram
{
    public static int Main(string[] args)
    {
        if (args is ["--validate-packages", var evidence])
        {
            try
            {
                var violations = PackageArtifacts.Violations(evidence).ToArray();
                foreach (var violation in violations)
                    Console.Error.WriteLine(violation);
                if (violations.Length != 0)
                    return 1;
                Console.WriteLine("Package artifacts and restored consumer closures passed.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"Package validation failed: {error.Message}");
                return 1;
            }
        }

        // Preserve xUnit's generated entrypoint, including Microsoft.Testing.Platform's MSBuild protocol.
        return args.Any(argument => argument is "--server" or "--internal-msbuild-node")
            ? Xunit
                .MicrosoftTestingPlatform.TestPlatformTestFramework.RunAsync(
                    args,
                    SelfRegisteredExtensions.AddSelfRegisteredExtensions
                )
                .GetAwaiter()
                .GetResult()
            : Xunit.Runner.InProc.SystemConsole.ConsoleRunner.Run(args).GetAwaiter().GetResult();
    }
}
