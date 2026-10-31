using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace PouryaKit.Localization;

// The request culture of every request, set by a startup filter ahead of every middleware the application adds, so its
// exception handler and endpoints run in it: of English and Persian, the one with the highest quality among the first 16
// values of the Accept-Language header, ties in listed order, a value of quality 0 never; English when they name neither or it
// is missing. The header is the only provider, so no query string or cookie changes it.
internal sealed class HeaderRequestCulture
    : IConfigureOptions<RequestLocalizationOptions>,
        IStartupFilter
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");
    private static readonly CultureInfo Persian = CultureInfo.GetCultureInfo("fa");

    public void Configure(RequestLocalizationOptions options)
    {
        options.DefaultRequestCulture = new RequestCulture(English);
        options.SupportedCultures = [English, Persian];
        options.SupportedUICultures = [English, Persian];
        options.RequestCultureProviders = [new AcceptLanguage()];
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        app =>
        {
            app.UseRequestLocalization();
            next(app);
        };

    // The framework's AcceptLanguageHeaderRequestCultureProvider, but for a value of quality 0, which RFC 9110 12.4.2 reads
    // as "not acceptable" and the framework would still try. The first 16 values the header lists are tried, not the
    // framework's first three, so Persian is found behind a browser's usual list; still a bound, as the framework's is, on
    // the cultures a long header makes a request try.
    private sealed class AcceptLanguage : RequestCultureProvider
    {
        private const int ValuesToTry = 16;

        public override Task<ProviderCultureResult?> DetermineProviderCultureResult(
            HttpContext httpContext
        )
        {
            ArgumentNullException.ThrowIfNull(httpContext);

            List<StringSegment> acceptable =
            [
                .. httpContext
                    .Request.GetTypedHeaders()
                    .AcceptLanguage.Take(ValuesToTry)
                    .Where(value => value.Quality is null or > 0)
                    .OrderByDescending(
                        value => value,
                        StringWithQualityHeaderValueComparer.QualityComparer
                    )
                    .Select(value => value.Value),
            ];
            return acceptable is []
                ? NullProviderCultureResult
                : Task.FromResult<ProviderCultureResult?>(new(acceptable));
        }
    }
}
