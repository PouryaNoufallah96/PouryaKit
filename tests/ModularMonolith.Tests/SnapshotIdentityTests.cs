using System.Globalization;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using ModularMonolith.Tests.Stub;
using PouryaKit.Domain;
using PouryaKit.Messaging.Wolverine.EFCore;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

public sealed class SnapshotIdentityTests(AppFixture app) : HostTest<AppFixture>(app)
{
    [Fact]
    public async Task Distinct_timestamp_keys_in_the_same_second_apply_both_snapshots_and_keep_identity_after_reload_and_removal()
    {
        var first = new DateTime(2026, 10, 6, 9, 0, 0, DateTimeKind.Unspecified).AddMilliseconds(100);
        var second = first.AddMilliseconds(100);
        await AssertKeysAsync("timestamp", [Text(first.Ticks), Text(second.Ticks)], value =>
            Fixture.Gateway<StubModule>().SendAsync(new ChangeTimestampKeysCommand([first, second], value), TestContext.Current.CancellationToken)
        );
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from stub.timestamp_keys"));
    }

    [Fact]
    public async Task Distinct_binary_keys_apply_both_snapshots_and_keep_identity_after_reload_and_removal()
    {
        await AssertKeysAsync("binary", ["0102", "0304"], value =>
            Fixture.Gateway<StubModule>().SendAsync(new ChangeBinaryKeysCommand([[1, 2], [3, 4]], value), TestContext.Current.CancellationToken)
        );
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from stub.binary_keys"));
    }

    [Fact]
    public async Task A_timestamp_key_below_postgres_precision_is_refused_before_the_change_or_snapshot_commits()
    {
        var key = new DateTime(2026, 10, 6, 9, 0, 0, DateTimeKind.Unspecified).AddTicks(1);
        var thrown = await Fixture.AssertNothingSentAsync(
            () => Assert.ThrowsAsync<InvalidOperationException>(() =>
                Fixture.Gateway<StubModule>().SendAsync(new ChangeTimestampKeysCommand([key], "first"), TestContext.Current.CancellationToken)
            ),
            TestContext.Current.CancellationToken
        );
        Assert.Contains("cannot round-trip its DateTime", thrown.Message);
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from stub.timestamp_keys"));
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from stub.pouryakit_receipts where subscription = 'stub.timestamp-key-snapshots'"));
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from stub.pouryakit_snapshot_versions where subscription = 'stub.timestamp-key-snapshots'"));
    }

    [Fact]
    public void Existing_scalar_formats_provider_conversion_and_composite_framing_are_invariant()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            var culture = (CultureInfo)CultureInfo.GetCultureInfo("fa-IR").Clone();
            culture.NumberFormat.NegativeSign = "minus";
            CultureInfo.CurrentCulture = culture;
            using var guid = new KeyContext<Guid>();
            using var integer = new KeyContext<int>();
            using var longInteger = new KeyContext<long>();
            using var text = new KeyContext<string>();
            using var converted = new KeyContext<CounterId>();
            using var composite = new CompositeKeyContext();
            var id = Guid.Parse("5da2a7fb-5577-494a-8ae1-44204f508f49");
            Assert.Equal(id.ToString(), Identity(guid.Entry(new KeyProbe<Guid>(id))));
            Assert.Equal("-12", Identity(integer.Entry(new KeyProbe<int>(-12))));
            Assert.Equal("-1234567890123", Identity(longInteger.Entry(new KeyProbe<long>(-1234567890123))));
            Assert.Equal("a:2/b", Identity(text.Entry(new KeyProbe<string>("a:2/b"))));
            Assert.Equal("-12", Identity(converted.Entry(new KeyProbe<CounterId>(new CounterId(-12)))));
            Assert.Equal("3:a:b0:", Identity(composite.Entry(new CompositeKeyProbe("a:b", ""))));
            Assert.Equal("1:a2:b:", Identity(composite.Entry(new CompositeKeyProbe("a", "b:"))));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void A_timestamp_identity_ignores_kind_but_refuses_values_or_mappings_that_lose_ticks()
    {
        var at = new DateTime(2026, 10, 6, 9, 0, 0, DateTimeKind.Utc).AddTicks(1_234_560);
        using var timestamp = new KeyContext<DateTime>();
        Assert.Equal(Text(at.Ticks), Identity(timestamp.Entry(new KeyProbe<DateTime>(at))));
        Assert.Equal(Text(at.Ticks), Identity(timestamp.Entry(new KeyProbe<DateTime>(DateTime.SpecifyKind(at, DateTimeKind.Unspecified)))));

        using var milliseconds = new MillisecondKeyContext();
        var lostPrecision = Refused(milliseconds.Entry(new KeyProbe<DateTime>(at)));
        Assert.Contains("cannot round-trip its DateTime", lostPrecision.Message);
        Assert.Equal(Text(at.Ticks - 4_560), Identity(milliseconds.Entry(new KeyProbe<DateTime>(at.AddTicks(-4_560)))));

        using var date = new DateKeyContext();
        Assert.Contains("cannot round-trip its DateTime", Refused(date.Entry(new KeyProbe<DateTime>(at.Date))).Message);
    }

    [Fact]
    public void An_uppercase_postgres_timestamp_mapping_keeps_the_key_identity_under_a_turkish_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var at = new DateTime(2026, 10, 6, 9, 0, 0, DateTimeKind.Unspecified).AddTicks(1_234_560);
            using var timestamp = new UppercaseTimestampKeyContext();
            Assert.Equal(Text(at.Ticks), Identity(timestamp.Entry(new KeyProbe<DateTime>(at))));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Null_and_unsupported_provider_values_are_refused_instead_of_using_arbitrary_text()
    {
        using var unsupported = new KeyContext<bool>();
        Assert.Contains("unsupported provider type System.Boolean", Refused(unsupported.Entry(new KeyProbe<bool>(true))).Message);
        using var text = new KeyContext<string>();
        Assert.Contains("unsupported provider type null", Refused(text.Entry(new KeyProbe<string>(null!))).Message);
    }

    private async Task AssertKeysAsync(string kind, string[] keys, Func<string?, Task<Result>> change)
    {
        var subscription = $"stub.{kind}-key-snapshots";
        var expectedKeys = keys.Order(StringComparer.Ordinal).ToArray();
        var missing = await Fixture.AssertNothingSentAsync(() => change(null), TestContext.Current.CancellationToken);
        Assert.True(missing.IsSuccess);
        Assert.Empty(await CopiesAsync(kind));
        Assert.Empty(await VersionsAsync(subscription));
        Assert.Equal(0L, await ReceiptCountAsync(subscription));

        var (created, _) = await Fixture.WaitForDeliveriesAsync(() => change("first"), TestContext.Current.CancellationToken);
        Assert.True(created.IsSuccess);
        Assert.Equal(expectedKeys.Select(key => new object[] { key, "first", 1L }), await CopiesAsync(kind));
        Assert.Equal(expectedKeys.Select(key => new object[] { key, 1L }), await VersionsAsync(subscription));
        Assert.Equal(2L, await ReceiptCountAsync(subscription));

        // Every call opens another Operation, which reloads the keys from PostgreSQL before changing them.
        var (updated, _) = await Fixture.WaitForDeliveriesAsync(() => change("second"), TestContext.Current.CancellationToken);
        Assert.True(updated.IsSuccess);
        Assert.Equal(expectedKeys.Select(key => new object[] { key, "second", 2L }), await CopiesAsync(kind));
        Assert.Equal(expectedKeys.Select(key => new object[] { key, 2L }), await VersionsAsync(subscription));
        Assert.Equal(4L, await ReceiptCountAsync(subscription));

        var (removed, _) = await Fixture.WaitForDeliveriesAsync(() => change(null), TestContext.Current.CancellationToken);
        Assert.True(removed.IsSuccess);
        Assert.Empty(await CopiesAsync(kind));
        Assert.Equal(expectedKeys.Select(key => new object[] { key, 3L }), await VersionsAsync(subscription));
        Assert.Equal(6L, await ReceiptCountAsync(subscription));

        var alreadyRemoved = await Fixture.AssertNothingSentAsync(() => change(null), TestContext.Current.CancellationToken);
        Assert.True(alreadyRemoved.IsSuccess);
        Assert.Empty(await CopiesAsync(kind));
        Assert.Equal(expectedKeys.Select(key => new object[] { key, 3L }), await VersionsAsync(subscription));
        Assert.Equal(6L, await ReceiptCountAsync(subscription));

        // Replay all three versions after the Tombstones. Neither the copy nor Receipts and versions may change.
        Assert.Equal(6, (await Fixture.RowsAsync($"update wolverine.wolverine_incoming_envelopes set status = 'Incoming', owner_id = 0 where received_at = 'local://{subscription}/' returning id")).Count);
        await Fixture.WaitUntilDeliveredAsync(TestContext.Current.CancellationToken);
        Assert.Empty(await CopiesAsync(kind));
        Assert.Equal(expectedKeys.Select(key => new object[] { key, 3L }), await VersionsAsync(subscription));
        Assert.Equal(6L, await ReceiptCountAsync(subscription));
    }

    private Task<List<object[]>> CopiesAsync(string kind) =>
        Fixture.RowsAsync($"select id, value, version from stub.snapshot_key_copies where kind = '{kind}' order by id collate \"C\"");

    private Task<List<object[]>> VersionsAsync(string subscription) =>
        Fixture.RowsAsync($"select aggregate_id, version from stub.pouryakit_snapshot_versions where subscription = '{subscription}' order by aggregate_id collate \"C\"");

    private Task<long> ReceiptCountAsync(string subscription) =>
        Fixture.ScalarAsync<long>($"select count(*) from stub.pouryakit_receipts where subscription = '{subscription}'");

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static MethodInfo AggregateId => typeof(SnapshotVersionTable).Assembly.GetType("PouryaKit.Messaging.Wolverine.EFCore.SnapshotStep")!.GetMethod("AggregateId", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static string Identity(EntityEntry entry) => (string)AggregateId.Invoke(null, [entry])!;

    private static InvalidOperationException Refused(EntityEntry entry) =>
        Assert.IsType<InvalidOperationException>(Assert.Throws<TargetInvocationException>(() => Identity(entry)).InnerException);

    private sealed class KeyProbe<T>(T id) : AggregateRoot<T> where T : notnull
    {
        public KeyProbe() : this(default!) { }

        public T Key { get; } = id;
    }

    private class KeyContext<T>() : DbContext(new DbContextOptionsBuilder().UseNpgsql("Host=localhost").Options) where T : notnull
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var key = modelBuilder.Entity<KeyProbe<T>>();
            key.Ignore(probe => probe.DomainEvents);
            key.Ignore(probe => probe.Id);
            key.HasKey(probe => probe.Key);
            var property = key.Property(probe => probe.Key).ValueGeneratedNever();
            if (typeof(T) == typeof(CounterId))
            {
                property.HasConversion(id => ((CounterId)(object)id!).Value, value => (T)(object)new CounterId(value));
            }
        }
    }

    private sealed class MillisecondKeyContext : KeyContext<DateTime>
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<KeyProbe<DateTime>>().Property(probe => probe.Key).HasPrecision(3);
        }
    }

    private sealed class UppercaseTimestampKeyContext : KeyContext<DateTime>
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<KeyProbe<DateTime>>().Property(probe => probe.Key).HasColumnType("TIMESTAMP");
        }
    }

    private sealed class DateKeyContext : KeyContext<DateTime>
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<KeyProbe<DateTime>>().Property(probe => probe.Key).HasColumnType("date");
        }
    }

    private sealed record CompositeKeyProbe(string First, string Second);

    private sealed class CompositeKeyContext() : DbContext(new DbContextOptionsBuilder().UseNpgsql("Host=localhost").Options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<CompositeKeyProbe>().HasKey(probe => new { probe.First, probe.Second });
    }
}
