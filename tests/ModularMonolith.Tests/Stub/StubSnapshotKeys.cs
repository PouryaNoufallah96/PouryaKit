using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Messaging;
using PouryaKit.Persistence;

namespace ModularMonolith.Tests.Stub;

internal sealed class TimestampKey : AggregateRoot<DateTime>
{
    private TimestampKey() { }

    public string Value { get; private set; } = "";

    public static TimestampKey Create(DateTime id, string value) => new() { Id = id, Value = value };

    public void Change(string value) => Value = value;
}

internal sealed class BinaryKey : AggregateRoot<byte[]>
{
    private BinaryKey() { }

    public string Value { get; private set; } = "";

    public static BinaryKey Create(byte[] id, string value) => new() { Id = id, Value = value };

    public void Change(string value) => Value = value;
}

internal sealed class TimestampKeyConfiguration : IEntityTypeConfiguration<TimestampKey>
{
    public void Configure(EntityTypeBuilder<TimestampKey> builder)
    {
        builder.ToTable("timestamp_keys");
        builder.Property(key => key.Id).HasColumnType("timestamp without time zone").ValueGeneratedNever();
    }
}

internal sealed class BinaryKeyConfiguration : IEntityTypeConfiguration<BinaryKey>
{
    public void Configure(EntityTypeBuilder<BinaryKey> builder)
    {
        builder.ToTable("binary_keys");
        builder.Property(key => key.Id).ValueGeneratedNever();
    }
}

internal sealed record TimestampKeySnapshotV1(DateTime Id, string Value, bool IsRemoved, long Version);

internal sealed record BinaryKeySnapshotV1(byte[] Id, string Value, bool IsRemoved, long Version);

internal sealed class TimestampKeySnapshotMapper : ISnapshotMapper<TimestampKey, TimestampKeySnapshotV1>
{
    public TimestampKeySnapshotV1 Map(TimestampKey key) => new(key.Id, key.Value, false, key.Version);

    public TimestampKeySnapshotV1 Tombstone(TimestampKey key) => new(key.Id, key.Value, true, key.Version);
}

internal sealed class BinaryKeySnapshotMapper : ISnapshotMapper<BinaryKey, BinaryKeySnapshotV1>
{
    public BinaryKeySnapshotV1 Map(BinaryKey key) => new(key.Id, key.Value, false, key.Version);

    public BinaryKeySnapshotV1 Tombstone(BinaryKey key) => new(key.Id, key.Value, true, key.Version);
}

internal sealed record ChangeTimestampKeysCommand(DateTime[] Keys, string? Value) : Command;

internal sealed class ChangeTimestampKeysCommandHandler(IRepository<TimestampKey, DateTime> keys) : CommandHandler<ChangeTimestampKeysCommand>
{
    public override async Task<Result> Handle(ChangeTimestampKeysCommand command, CancellationToken cancellationToken)
    {
        foreach (var id in command.Keys)
        {
            var key = await keys.FindAsync(id, cancellationToken);
            if (command.Value is { } value)
            {
                if (key is null)
                {
                    await keys.AddAsync(TimestampKey.Create(id, value), cancellationToken);
                }
                else
                {
                    key.Change(value);
                }
            }
            else if (key is not null)
            {
                keys.Remove(key);
            }
        }

        return Result.Success();
    }
}

internal sealed record ChangeBinaryKeysCommand(byte[][] Keys, string? Value) : Command;

internal sealed class ChangeBinaryKeysCommandHandler(IRepository<BinaryKey, byte[]> keys) : CommandHandler<ChangeBinaryKeysCommand>
{
    public override async Task<Result> Handle(ChangeBinaryKeysCommand command, CancellationToken cancellationToken)
    {
        foreach (var id in command.Keys)
        {
            var key = await keys.FindAsync(id, cancellationToken);
            if (command.Value is { } value)
            {
                if (key is null)
                {
                    await keys.AddAsync(BinaryKey.Create(id, value), cancellationToken);
                }
                else
                {
                    key.Change(value);
                }
            }
            else if (key is not null)
            {
                keys.Remove(key);
            }
        }

        return Result.Success();
    }
}

internal sealed class CopyTimestampKeyIntegrationEventHandler(IUnitOfWorkConnection unitOfWork) : IIntegrationEventHandler<TimestampKeySnapshotV1>
{
    public Task<Result> Handle(TimestampKeySnapshotV1 snapshot, CancellationToken cancellationToken) =>
        SnapshotKeyCopies.ApplyAsync(unitOfWork, "timestamp", snapshot.Id.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture), snapshot.Value, snapshot.IsRemoved, snapshot.Version, cancellationToken);
}

internal sealed class CopyBinaryKeyIntegrationEventHandler(IUnitOfWorkConnection unitOfWork) : IIntegrationEventHandler<BinaryKeySnapshotV1>
{
    public Task<Result> Handle(BinaryKeySnapshotV1 snapshot, CancellationToken cancellationToken) =>
        SnapshotKeyCopies.ApplyAsync(unitOfWork, "binary", Convert.ToHexString(snapshot.Id), snapshot.Value, snapshot.IsRemoved, snapshot.Version, cancellationToken);
}

internal static class SnapshotKeyCopies
{
    public static async Task<Result> ApplyAsync(IUnitOfWorkConnection unitOfWork, string kind, string id, string value, bool removed, long version, CancellationToken cancellationToken)
    {
        var (connection, transaction) = unitOfWork.Join();
        var sql = removed
            ? "delete from stub.snapshot_key_copies where kind = @kind and id = @id"
            : """
              insert into stub.snapshot_key_copies (kind, id, value, version) values (@kind, @id, @value, @version)
              on conflict (kind, id) do update set value = excluded.value, version = excluded.version
              """;
        await connection.ExecuteAsync(new CommandDefinition(sql, new { kind, id, value, version }, transaction, cancellationToken: cancellationToken));
        return Result.Success();
    }
}
