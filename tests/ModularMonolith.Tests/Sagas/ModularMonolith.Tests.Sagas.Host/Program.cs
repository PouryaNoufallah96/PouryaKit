using JasperFx;
using ModularMonolith.Tests.Sagas.Host;

// The JasperFx command line over the Host, codegen write among it, which package validation runs before the Release
// publish; with no command, the Host runs.
return await SagaHost.Compose(args).Build().RunJasperFxCommands(args);
