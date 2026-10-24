using JasperFx;
using ModularMonolith.Host;

// Runs the Host, or a JasperFx command such as `codegen write`, which writes Wolverine's generated code before a release
// build (Wolverine's code generation guide).
return await AppHost.Build(AppHost.CreateBuilder(args)).RunJasperFxCommands(args);
