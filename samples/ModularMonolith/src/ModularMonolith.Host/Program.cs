using JasperFx;
using ModularMonolith.Host;

return await ModularMonolithHost.Build(ModularMonolithHost.CreateBuilder(args)).RunJasperFxCommands(args);
