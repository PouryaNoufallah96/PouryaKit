using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Options;

namespace PouryaKit.Localization;

// The request culture of every request, set by a startup filter ahead of every middleware the application adds, so its
// exception handler and endpoints run in it: the first of English and Persian the Accept-Language header names, English
// when it names neither or is missing. The header is the only provider, so no query string or cookie changes it.
internal sealed class HeaderRequestCulture : IConfigureOptions<RequestLocalizationOptions>, IStartupFilter
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");
    private static readonly CultureInfo Persian = CultureInfo.GetCultureInfo("fa");

    public void Configure(RequestLocalizationOptions options)
    {
        options.DefaultRequestCulture = new RequestCulture(English);
        options.SupportedCultures = [English, Persian];
        options.SupportedUICultures = [English, Persian];
        options.RequestCultureProviders = [new AcceptLanguageHeaderRequestCultureProvider()];
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        app =>
        {
            app.UseRequestLocalization();
            next(app);
        };
}
