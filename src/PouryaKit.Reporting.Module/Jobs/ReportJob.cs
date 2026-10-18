using PouryaKit.Cqrs;
using PouryaKit.Domain;

namespace PouryaKit.Reporting.Module;

internal sealed record ReportJobId(Guid Value) : TypedId<Guid>(Value);

internal enum ReportJobState
{
    Requested,
    Generating,
    Completed,
    Failed,
    Expired,
}

// One requested report, from its request to its expiry. Each claim gives a generation attempt the next number, and only
// the attempt holding the current claim settles the job; the Version every save checks fences a claim, a settlement and
// an expiry against any change committed after the Operation read the job. Each attempt stores its file under its own
// key, so only the key a settlement published is ever served.
internal sealed class ReportJob : AggregateRoot<ReportJobId>
{
    private ReportJob() { }

    public string Kind { get; private set; } = "";

    public ReportFormat Format { get; private set; }

    public Dictionary<string, string> Parameters { get; private set; } = [];

    public string RequestedBy { get; private set; } = "";

    public ActorKind RequestedByKind { get; private set; }

    public string? Tenant { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public ReportJobState State { get; private set; }

    public int Attempt { get; private set; }

    public DateTimeOffset? ClaimedAt { get; private set; }

    public string? FileKey { get; private set; }

    public string? Failure { get; private set; }

    public ReportRequest ToRequest() => new(Kind, Format, Parameters);

    public static ReportJob Request(ReportJobId id, ReportRequest request, OperationContext requester, DateTimeOffset now, TimeSpan retention) =>
        new()
        {
            Id = id,
            Kind = request.Kind,
            Format = request.Format,
            Parameters = new(request.Parameters),
            RequestedBy = requester.Actor.Id,
            RequestedByKind = requester.Actor.Kind,
            Tenant = requester.Tenant,
            RequestedAt = now,
            ExpiresAt = now + retention,
            State = ReportJobState.Requested,
        };

    // The first claim takes a requested job; a later one takes the job over only from the attempt before it, once that
    // attempt's claim is stale, so each claim is taken over at most once.
    public bool TryClaim(int attempt, DateTimeOffset now, TimeSpan staleAfter)
    {
        var claimable = State switch
        {
            ReportJobState.Requested => attempt == 1,
            ReportJobState.Generating => attempt == Attempt + 1 && now - ClaimedAt >= staleAfter,
            _ => false,
        };
        if (claimable)
        {
            State = ReportJobState.Generating;
            Attempt = attempt;
            ClaimedAt = now;
        }

        return claimable;
    }

    public bool IsClaimedBy(int attempt) => State == ReportJobState.Generating && Attempt == attempt;

    public string FileKeyOf(int attempt) => $"reports/{Id.Value:N}/{attempt}";

    // Every key an attempt of this job may have stored its file under.
    public IEnumerable<string> FileKeys => Enumerable.Range(1, Attempt).Select(FileKeyOf);

    public void Complete(int attempt, string fileKey)
    {
        EnsureClaimedBy(attempt);
        State = ReportJobState.Completed;
        FileKey = fileKey;
    }

    public void Fail(int attempt, Error error)
    {
        EnsureClaimedBy(attempt);
        State = ReportJobState.Failed;
        Failure = error.Code;
    }

    // An expired job serves nothing, so its files may go once the expiry is committed. A job whose attempt holds a claim
    // that is not yet stale is left until it is: that attempt may still store its file.
    public bool TryExpire(DateTimeOffset now, TimeSpan staleAfter)
    {
        if (State == ReportJobState.Expired || ExpiresAt > now || (State == ReportJobState.Generating && now - ClaimedAt < staleAfter))
        {
            return false;
        }

        State = ReportJobState.Expired;
        FileKey = null;
        return true;
    }

    public bool IsRequestedBy(Actor actor) => RequestedBy == actor.Id && RequestedByKind == actor.Kind;

    private void EnsureClaimedBy(int attempt)
    {
        if (!IsClaimedBy(attempt))
        {
            throw new InvalidOperationException($"Attempt {attempt} does not hold the claim on the report job {Id.Value}.");
        }
    }
}
