using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Npgsql;
using PouryaKit.Testing;
using Testcontainers.Papercut;

namespace ModularMonolith.Tests.Fixtures;

/// <summary>
/// A Papercut SMTP server, which keeps every mail it accepts and lists it over HTTP, and the application started on a
/// database of its own with its email sent there: the fixture's Host, on its own database, would take none of these
/// deliveries, and none of its own reach Papercut.
/// </summary>
public sealed class MailFixture(AppFixture app) : IAsyncLifetime
{
    private readonly PapercutContainer _papercut = new PapercutBuilder(
        "changemakerstudiosus/papercut-smtp:7.8.0"
    ).Build();
    private HttpClient? _api;
    private WebApplication? _host;
    private string? _connectionString;

    /// <summary>The started application, whose <c>IEmailSender</c> sends to Papercut.</summary>
    public WebApplication Host =>
        _host ?? throw new InvalidOperationException("The Host starts in InitializeAsync.");

    public async ValueTask InitializeAsync()
    {
        await _papercut.StartAsync(TestContext.Current.CancellationToken);
        _api = new HttpClient { BaseAddress = new Uri(_papercut.GetBaseAddress()) };
        var database = $"mail_{Guid.CreateVersion7():N}";
        await app.RowsAsync($"create database {database}");
        _connectionString = new NpgsqlConnectionStringBuilder(app.ConnectionString)
        {
            Database = database,
        }.ConnectionString;
        _host = AppFixture.ComposeHost(
            "Development",
            _connectionString,
            new RecordedLogs(),
            null,
            $"--PouryaKit:Email:Host={_papercut.Hostname}",
            $"--PouryaKit:Email:Port={_papercut.SmtpPort}",
            "--PouryaKit:Email:Security=None"
        );
        await HostFixture.StartHostAsync(_host, TestContext.Current.CancellationToken);
    }

    /// <summary>Waits until the SQL <paramref name="condition"/> holds on the application's database; fails after 30 s.</summary>
    public Task WaitUntilAsync(string condition, string because) =>
        Sql.WaitUntilAsync(
            _connectionString
                ?? throw new InvalidOperationException(
                    "The database is created in InitializeAsync."
                ),
            condition,
            because,
            30
        );

    /// <summary>Every mail Papercut has received with the subject <paramref name="subject"/>, read through its API.</summary>
    public async Task<IReadOnlyList<ReceivedMail>> MailsAsync(string subject)
    {
        var api =
            _api ?? throw new InvalidOperationException("Papercut starts in InitializeAsync.");
        var listed = await api.GetFromJsonAsync<MailList>(
            "/api/messages",
            TestContext.Current.CancellationToken
        );
        List<ReceivedMail> mails = [];
        foreach (var summary in listed!.Messages.Where(summary => summary.Subject == subject))
        {
            mails.Add(
                (
                    await api.GetFromJsonAsync<ReceivedMail>(
                        $"/api/messages/{Uri.EscapeDataString(summary.Id)}",
                        TestContext.Current.CancellationToken
                    )
                )!
            );
        }

        return mails;
    }

    /// <summary>Waits until Papercut holds <paramref name="count"/> mails with <paramref name="subject"/>; fails after 30 s.</summary>
    public async Task<IReadOnlyList<ReceivedMail>> WaitForMailsAsync(string subject, int count)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            var mails = await MailsAsync(subject);
            if (mails.Count >= count)
            {
                return mails;
            }

            Assert.False(
                timeout.IsCancellationRequested,
                $"{mails.Count} of {count} mails with the subject {subject} arrived in 30 s."
            );
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_host is not null)
            {
                await _host.StopAsync(TestContext.Current.CancellationToken);
                await _host.DisposeAsync();
            }
        }
        finally
        {
            _api?.Dispose();
            await _papercut.DisposeAsync();
        }
    }

    private sealed record MailList(IReadOnlyList<MailSummary> Messages);

    private sealed record MailSummary(string Id, string Subject);
}

/// <summary>A mail as Papercut's API shows it.</summary>
public sealed record ReceivedMail(
    string Subject,
    IReadOnlyList<MailboxOf> From,
    IReadOnlyList<MailboxOf> To,
    string? TextBody
);

/// <summary>One address of a <see cref="ReceivedMail"/>.</summary>
public sealed record MailboxOf(string Name, string Address);
