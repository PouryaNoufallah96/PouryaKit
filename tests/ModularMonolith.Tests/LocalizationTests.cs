using System.Net.Http.Json;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

// A Module's Feature writes one message key through its localizer; the client's Accept-Language header picks the
// culture it resolves in, English when it names no culture the Host supports.
public sealed class LocalizationTests(AppFixture app) : HostTest<AppFixture>(app)
{
    private const string English = "Welcome";
    private const string Persian = "خوش آمدید";

    [Theory]
    [InlineData(null, English)]
    [InlineData("en", English)]
    [InlineData("fa", Persian)]
    [InlineData("fa-IR", Persian)]
    [InlineData("de, fa;q=0.5", Persian)]
    [InlineData("de", English)]
    public async Task The_same_key_resolves_in_the_culture_of_the_accept_language_header(string? acceptLanguage, string expected)
    {
        using var client = Fixture.Client("localized-caller");
        if (acceptLanguage is not null)
        {
            client.DefaultRequestHeaders.Add("Accept-Language", acceptLanguage);
        }

        var message = await client.GetFromJsonAsync<string>("/stub/greeting", TestContext.Current.CancellationToken);

        Assert.Equal(expected, message);
    }
}
