using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using PouryaKit.Localization;

// The packed Localization, registered as an application without Modules would, naming its Feature assembly: English
// and Persian, with the request culture from the Accept-Language header alone.
var services = new ServiceCollection().AddLogging().AddPouryaKitLocalization(typeof(OrderMessages).Assembly);
using var provider = services.BuildServiceProvider();
var cultures = provider.GetRequiredService<IOptions<RequestLocalizationOptions>>().Value;
var messages = provider.GetRequiredService<IStringLocalizer<OrderMessages>>();
Console.WriteLine(cultures.SupportedUICultures!.Count == 2 && cultures.RequestCultureProviders.Count == 1 && messages["Placed"].ResourceNotFound ? 1 : 0);

internal sealed class OrderMessages;
