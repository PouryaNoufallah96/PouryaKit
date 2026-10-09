using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace PouryaKit.Web;

// The compression middleware compresses every HTTP response it can once it is in the pipeline: with no providers or
// content types configured it uses the defaults. So it is added, outermost, only when the options enable it, before
// anything that writes a response, as the middleware requires.
internal sealed class ConfiguredCompression : IStartupFilter
{
    public static void Add(IServiceCollection services)
    {
        services.AddResponseCompression();
        services
            .AddOptions<ResponseCompressionOptions>()
            .Configure<IOptions<PouryaKitCompressionOptions>>(
                (compression, configured) => compression.EnableForHttps |= configured.Value.Enabled
            );
        services.TryAddEnumerable(ServiceDescriptor.Transient<IStartupFilter, ConfiguredCompression>());
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        app =>
        {
            if (app.ApplicationServices.GetRequiredService<IOptions<PouryaKitCompressionOptions>>().Value.Enabled)
            {
                app.UseResponseCompression();
            }

            next(app);
        };
}
