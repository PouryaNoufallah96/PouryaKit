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
    [InlineData("en;q=0.1, fa;q=1", Persian)]
    [InlineData("de, fr, it, fa", Persian)]
    [InlineData("de, fr, it, es, pt, nl, sv, da, nb, fi, pl, cs, hu, ro, tr, fa", Persian)]
    [InlineData("de, fr, it, es, pt, nl, sv, da, nb, fi, pl, cs, hu, ro, tr, el, fa", English)]
    [InlineData("de", English)]
    [InlineData("fa;q=0", English)]
    [InlineData("fa;q=0, en;q=0", English)]
    public async Task The_same_key_resolves_in_the_culture_of_the_accept_language_header(
        string? acceptLanguage,
        string expected
    )
    {
        using var client = Fixture.Client("localized-caller");
        if (acceptLanguage is not null)
        {
            client.DefaultRequestHeaders.Add("Accept-Language", acceptLanguage);
        }

        var message = await client.GetFromJsonAsync<string>(
            "/stub/greeting",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(expected, message);
    }

    [Fact]
    public async Task The_query_string_does_not_set_the_culture()
    {
        using var client = Fixture.Client("localized-caller");

        var message = await client.GetFromJsonAsync<string>(
            "/stub/greeting?culture=fa&ui-culture=fa",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(English, message);
    }

    [Fact]
    public async Task A_cached_localized_query_answers_each_caller_in_their_own_culture()
    {
        var caller = $"cached-{Guid.CreateVersion7()}";
        using var persian = Fixture.Client(caller);
        persian.DefaultRequestHeaders.Add("Accept-Language", "fa");
        using var english = Fixture.Client(caller);
        english.DefaultRequestHeaders.Add("Accept-Language", "en");

        var first = await persian.GetFromJsonAsync<string>(
            "/stub/greeting/cached",
            TestContext.Current.CancellationToken
        );
        var second = await english.GetFromJsonAsync<string>(
            "/stub/greeting/cached",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(Persian, first);
        Assert.Equal(English, second);
    }
}
