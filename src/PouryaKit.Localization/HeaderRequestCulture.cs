using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Options;

namespace PouryaKit.Localization;

// The request culture of every request, set by a startup filter ahead of every middleware the application adds, so its
// exception handler and endpoints run in it: of English and Persian, the one with the highest quality among the first 16
// values of the Accept-Language header, ties in listed order; English when they name neither or it is missing. The header
// is the only provider, so no query string or cookie changes it.
internal sealed class HeaderRequestCulture : IConfigureOptions<RequestLocalizationOptions>, IStartupFilter
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");
    private static readonly CultureInfo Persian = CultureInfo.GetCultureInfo("fa");

    public void Configure(RequestLocalizationOptions options)
    {
        options.DefaultRequestCulture = new RequestCulture(English);
        options.SupportedCultures = [English, Persian];
        options.SupportedUICultures = [English, Persian];
        // The first 16 values the header lists are tried, not the framework's first three, so Persian is found behind a
        // browser's usual list; still a bound, as the framework's is, on the cultures a long header makes a request try.
        options.RequestCultureProviders = [new AcceptLanguageHeaderRequestCultureProvider { MaximumAcceptLanguageHeaderValuesToTry = 16 }];
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        app =>
        {
            app.UseRequestLocalization();
            next(app);
        };
}
