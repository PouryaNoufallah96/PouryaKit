using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ModularMonolith.Host;
using PouryaKit.Cqrs;
using PouryaKit.Reporting;
using PouryaKit.Reporting.Module;
using PouryaKit.Storage;
using Wolverine.Runtime.Serialization;

namespace ModularMonolith.Tests;

// Ticket #140: the report Module, the first Module the Kit ships as a package, over HTTP against PostgreSQL and a RustFS
// bucket. A request is authorized by the Host's port before any job exists; generation runs in the background as a
// continuation of the request, through the renderer into the storage contract; claims are fenced, so a stale attempt
// is taken over once and its late settlement changes nothing the job serves; only the requester within the job's
// tenant, or an Actor granted the tenant's jobs, reads it; retention removes expired jobs' files in every tenant and
// never the file a job serves.
public sealed class ReportModuleTests(ReportFixture reports) : IClassFixture<ReportFixture>
{
    private const string Tenant = "acme";
    private const string OtherTenant = "globex";

    [Fact]
    public async Task A_request_answers_202_with_the_job_whose_file_the_background_renders_and_stores_for_its_requester()
    {
        using var ada = reports.Client("ada", Tenant);

        var requested = await ada.PostAsJsonAsync("/reports", Sales, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        var id = (await requested.Content.ReadFromJsonAsync<Accepted>(TestContext.Current.CancellationToken))!.Id;
        Assert.Equal($"/reports/{id}", requested.Headers.Location?.OriginalString);
        var job = await CompletedAsync(ada, id);
        Assert.Equal(("sales", "Csv"), (job.Kind, job.Format));
        // The file is the renderer's output of the Host's layout, read without credentials through the link.
        var layout = await new SalesReports().LayOutAsync(
            new OperationContext(Actor.Person("ada"), Tenant),
            new ReportRequest(SalesReports.Kind, ReportFormat.Csv, Sales.Parameters),
            TestContext.Current.CancellationToken
        );
        using var download = new HttpClient();
        Assert.Equal(Csv(layout.Value), await download.GetByteArrayAsync(job.DownloadUrl, TestContext.Current.CancellationToken));
        // The layout was asked for under a continuation of the request, its Actor, tenant and correlation, by every attempt,
        // a takeover's included should a slow machine let the claim go stale.
        var request = reports.Reports.Authorized.Last();
        var laidOut = reports.Reports.LaidOut.Where(context => context.CorrelationId == request.CorrelationId).ToList();
        Assert.NotEmpty(laidOut);
        Assert.All(laidOut, context => Assert.Equal((Actor.Person("ada"), Tenant, true), (context.Actor, context.Tenant, context.IsTrustedContinuation)));
    }

    [Fact]
    public async Task A_request_the_hosts_port_refuses_answers_403_and_stores_no_job_and_schedules_nothing()
    {
        using var ada = reports.Client("ada", Tenant);
        var claims = await ClaimsAsync();

        var refused = await ada.PostAsJsonAsync(
            "/reports",
            Sales with { Parameters = new() { [SalesReports.Month] = "October" } },
            TestContext.Current.CancellationToken
        );

        Assert.Equal("Reports.RequestDenied", await ErrorCodeAsync(refused, HttpStatusCode.Forbidden));
        Assert.Empty(await reports.RowsAsync("select id from reports.report_jobs where parameters ->> 'month' = 'October'"));
        Assert.Equal(claims.Count, (await ClaimsAsync()).Count);
    }

    [Fact]
    public async Task Only_the_requester_within_the_jobs_tenant_or_an_actor_granted_the_tenants_jobs_reads_it_and_no_one_else_gets_a_link()
    {
        using var ada = reports.Client("ada", Tenant);
        var id = await RequestAsync(ada, Sales);
        await CompletedAsync(ada, id);
        var signed = reports.Storage.Signed.Count;

        using var bob = reports.Client("bob", Tenant);
        using var adaElsewhere = reports.Client("ada", OtherTenant);
        using var grantedElsewhere = reports.Client("carol", OtherTenant, ReportModule.ReadTenantJobsPermission);
        // Each caller's answer, so a check that admits one shows which: the permission, which spec #115 lets the Host
        // grant, reads its own tenant's jobs alone.
        List<(string Caller, HttpStatusCode Status)> reads = [];
        foreach (var (caller, other) in new[] { ("bob", bob), ("ada elsewhere", adaElsewhere), ("granted elsewhere", grantedElsewhere) })
        {
            var read = await other.GetAsync($"/reports/{id}", TestContext.Current.CancellationToken);
            reads.Add((caller, read.StatusCode));
            if (read.StatusCode == HttpStatusCode.NotFound)
            {
                Assert.Equal("Reports.JobNotFound", await ErrorCodeAsync(read, HttpStatusCode.NotFound));
            }
        }

        Assert.Equal([("bob", HttpStatusCode.NotFound), ("ada elsewhere", HttpStatusCode.NotFound), ("granted elsewhere", HttpStatusCode.NotFound)], reads);

        // Nothing was signed for anyone who may not read the job.
        Assert.Equal(signed, reports.Storage.Signed.Count);
        using var carol = reports.Client("carol", Tenant, ReportModule.ReadTenantJobsPermission);
        var granted = await carol.GetFromJsonAsync<Job>($"/reports/{id}", TestContext.Current.CancellationToken);
        Assert.Equal(("Completed", true), (granted!.State, granted.DownloadUrl is not null));
    }

    [Fact]
    public async Task A_stale_claim_is_taken_over_once_its_late_settlement_is_refused_and_the_winning_attempts_file_is_served()
    {
        using var ada = reports.Client("ada", Tenant);
        var id = Guid.Empty;
        try
        {
            id = await RequestAsync(ada, new Request(TestReports.Held, "Csv", []));
            // The first attempt waits in its layout past its claim's timeout; the second takes the job over and settles.
            await reports.WaitUntilAsync(
                $"exists (select from reports.report_jobs where id = '{id}' and attempt = 2 and state = 'Completed')",
                "The stale claim was not taken over."
            );
        }
        finally
        {
            reports.Reports.ReleaseHeld();
        }

        // The first attempt goes on: it stores its file under its own key, and its settlement is refused.
        await reports.WaitUntilAsync(
            "not exists (select from wolverine.wolverine_incoming_envelopes where received_at = 'local://reports.render/' and status <> 'Handled')",
            "The first attempt did not finish."
        );
        var served = await CompletedAsync(ada, id);
        using var download = new HttpClient();
        Assert.Equal(Csv(TestReports.HeldLayout(2)), await download.GetByteArrayAsync(served.DownloadUrl, TestContext.Current.CancellationToken));
        // Its retry, finding the claim lost, removed the file the late upload stored under its own key.
        Assert.Contains($"reports/{id:N}/1", reports.Storage.Uploaded);
        await NotStoredAsync($"reports/{id:N}/1");
        Assert.Equal([[2, $"reports/{id:N}/2"]], await reports.RowsAsync($"select attempt, file_key from reports.report_jobs where id = '{id}'"));
        // Taken over once: the first claim scheduled the one takeover, and the takeover scheduled none.
        Assert.Equal([1, 2], (await ClaimsAsync()).Where(claim => claim.JobId == id).Select(claim => claim.Attempt).Order());
        Assert.Equal(2, reports.Reports.HeldCalls);
    }

    [Fact]
    public async Task Retention_removes_the_expired_jobs_and_files_of_every_tenant_and_never_a_file_a_job_serves()
    {
        using var ada = reports.Client("ada", Tenant);
        using var bea = reports.Client("bea", OtherTenant);
        var (adas, beas, kept) = (await RequestAsync(ada, Sales), await RequestAsync(bea, Sales), await RequestAsync(ada, Sales));
        await CompletedAsync(ada, adas);
        await CompletedAsync(bea, beas);
        await CompletedAsync(ada, kept);
        var expiredFiles = (await reports.RowsAsync($"select file_key from reports.report_jobs where id in ('{adas}', '{beas}')"))
            .Select(row => (string)row[0])
            .ToList();

        await reports.RowsAsync($"update reports.report_jobs set expires_at = now() - interval '1 second' where id in ('{adas}', '{beas}')");

        await reports.WaitUntilAsync(
            $"not exists (select from reports.report_jobs where id in ('{adas}', '{beas}'))",
            "Retention did not remove the expired jobs."
        );
        Assert.Equal(2, expiredFiles.Count);
        foreach (var file in expiredFiles)
        {
            var downloaded = await reports.Storage.Provider.DownloadAsync(file, TestContext.Current.CancellationToken);
            Assert.Equal(StorageErrors.FileNotFound, Assert.Single(downloaded.Errors));
        }

        var served = await CompletedAsync(ada, kept);
        using var download = new HttpClient();
        Assert.NotEmpty(await download.GetByteArrayAsync(served.DownloadUrl, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_upload_that_outlives_its_jobs_expiry_and_removal_leaves_no_file_behind()
    {
        using var ada = reports.Client("ada", Tenant);
        var id = Guid.Empty;
        try
        {
            id = await RequestAsync(ada, new Request(TestReports.Stalled, "Csv", []));
            // Both attempts wait in their layouts: the first past its claim's timeout, then the takeover past its own.
            await reports.WaitUntilAsync(
                $"exists (select from reports.report_jobs where id = '{id}' and attempt = 2)",
                "The stale claim was not taken over."
            );
            await reports.RowsAsync($"update reports.report_jobs set expires_at = now() - interval '1 second' where id = '{id}'");
            await reports.WaitUntilAsync(
                $"not exists (select from reports.report_jobs where id = '{id}')",
                "Retention did not remove the expired job."
            );
            Assert.Equal(2, reports.Reports.StalledCalls);
        }
        finally
        {
            reports.Reports.ReleaseStalled();
        }

        // Each attempt then stores its file under its own key, after retention deleted the job's files and the job; its
        // settlement finds the job gone, and its retry removes the file.
        await reports.WaitUntilAsync(
            "not exists (select from wolverine.wolverine_incoming_envelopes where received_at = 'local://reports.render/' and status <> 'Handled')",
            "An attempt did not finish."
        );
        string[] keys = [$"reports/{id:N}/1", $"reports/{id:N}/2"];
        Assert.Equal(keys, reports.Storage.Uploaded.Where(keys.Contains).Order(StringComparer.Ordinal));
        foreach (var key in keys)
        {
            await NotStoredAsync(key);
        }
    }

    [Fact]
    public async Task A_request_with_no_format_or_an_unknown_one_answers_400_and_stores_no_job()
    {
        using var ada = reports.Client("ada", Tenant);
        var before = await reports.RowsAsync("select count(*) from reports.report_jobs");

        var missing = await ada.PostAsJsonAsync(
            "/reports",
            new { kind = SalesReports.Kind, parameters = Sales.Parameters },
            TestContext.Current.CancellationToken
        );
        var unknown = await ada.PostAsJsonAsync("/reports", Sales with { Format = "Docx" }, TestContext.Current.CancellationToken);

        await ProblemResponses.ProblemAsync(missing, HttpStatusCode.BadRequest);
        await ProblemResponses.ProblemAsync(unknown, HttpStatusCode.BadRequest);
        Assert.Equal(before, await reports.RowsAsync("select count(*) from reports.report_jobs"));
    }

    private static readonly Request Sales = new(SalesReports.Kind, "Csv", new() { [SalesReports.Month] = "2026-10" });

    private static byte[] Csv(ReportLayout layout) => new ReportRenderer().Render(layout, ReportFormat.Csv).Value.Content;

    private static async Task<Guid> RequestAsync(HttpClient client, Request request)
    {
        var requested = await client.PostAsJsonAsync("/reports", request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        return (await requested.Content.ReadFromJsonAsync<Accepted>(TestContext.Current.CancellationToken))!.Id;
    }

    // The job as its requester reads it once it completed, polled over HTTP.
    private static async Task<Job> CompletedAsync(HttpClient client, Guid id)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            var job = await client.GetFromJsonAsync<Job>($"/reports/{id}", TestContext.Current.CancellationToken);
            if (job!.State == "Completed")
            {
                return job;
            }

            Assert.False(timeout.IsCancellationRequested, $"The job {id} is still {job.State} after 30 s.");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        var problem = await ProblemResponses.ProblemAsync(response, status);
        return Assert.Single(problem.GetProperty("errors").EnumerateArray()).GetProperty("code").GetString()!;
    }

    // Every claim the Module stored, of any job, read from the message store's envelopes as the Kit's JSON wrote them.
    private async Task<List<Claim>> ClaimsAsync() =>
    [
        .. (await reports.RowsAsync("select body from wolverine.wolverine_incoming_envelopes where received_at = 'local://reports.claim/'")).Select(row =>
        {
            using var claim = JsonDocument.Parse(EnvelopeSerializer.Deserialize((byte[])row[0]).Data!);
            return new Claim(claim.RootElement.GetProperty("jobId").GetGuid(), claim.RootElement.GetProperty("attempt").GetInt32());
        }),
    ];

    private async Task NotStoredAsync(string key)
    {
        var stored = await reports.Storage.Provider.DownloadAsync(key, TestContext.Current.CancellationToken);
        Assert.Equal(StorageErrors.FileNotFound, Assert.Single(stored.Errors));
    }

    private sealed record Request(string Kind, string Format, Dictionary<string, string> Parameters);

    private sealed record Accepted(Guid Id);

    private sealed record Job(Guid Id, string Kind, string Format, string State, Uri? DownloadUrl, string? Failure, DateTimeOffset ExpiresAt);

    private sealed record Claim(Guid JobId, int Attempt);
}
