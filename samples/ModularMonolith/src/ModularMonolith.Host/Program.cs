using Microsoft.Extensions.Hosting;
using ModularMonolith.Host;

await ModularMonolithHost.CreateBuilder(args).Build().RunAsync();
