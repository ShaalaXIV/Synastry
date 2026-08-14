using Microsoft.Data.Sqlite;

namespace EmoteLink.Relay;

/// <summary>
/// Stores anonymous relay-wide counters. Only aggregate lifetime totals are persisted;
/// the active-user gauge exists in memory and resets to zero when the relay restarts.
/// </summary>
public sealed class RelayStatisticsStore
{
    private readonly RelayDatabase database;
    private readonly ILogger<RelayStatisticsStore> logger;
    private readonly object gate = new();
    private long roomsGenerated;
    private long sharedAnimations;
    private long animationsPerformed;
    private int activeUsers;

    public RelayStatisticsStore(RelayDatabase database, ILogger<RelayStatisticsStore> logger)
    {
        this.database = database;
        this.logger = logger;
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT rooms_generated, shared_animations, animations_performed
            FROM relay_statistics
            WHERE singleton = 1;
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException("Relay statistics storage is unavailable.");
        roomsGenerated = reader.GetInt64(0);
        sharedAnimations = reader.GetInt64(1);
        animationsPerformed = reader.GetInt64(2);
    }

    public RelayStatisticsDto GetSnapshot()
    {
        lock (gate)
            return SnapshotUnsafe();
    }

    public RelayStatisticsDto ConnectionOpened()
    {
        Interlocked.Increment(ref activeUsers);
        return GetSnapshot();
    }

    public RelayStatisticsDto ConnectionClosed()
    {
        while (true)
        {
            var current = Volatile.Read(ref activeUsers);
            if (current == 0 || Interlocked.CompareExchange(ref activeUsers, current - 1, current) == current)
                return GetSnapshot();
        }
    }

    public RelayStatisticsDto IncrementRoomsGenerated() => IncrementCounter(
        "rooms_generated", 1, static (store, amount) => store.roomsGenerated += amount);

    public RelayStatisticsDto IncrementSharedAnimations() => IncrementCounter(
        "shared_animations", 1, static (store, amount) => store.sharedAnimations += amount);

    public RelayStatisticsDto IncrementAnimationsPerformed(int amount)
    {
        if (amount < 1) return GetSnapshot();
        return IncrementCounter(
            "animations_performed", amount, static (store, value) => store.animationsPerformed += value);
    }

    private RelayStatisticsDto IncrementCounter(
        string column, long amount, Action<RelayStatisticsStore, long> updateMemory)
    {
        lock (gate)
        {
            try
            {
                using var connection = database.OpenConnection();
                using var command = connection.CreateCommand();
                // Column names are private constants supplied only by the strongly typed methods above.
                command.CommandText =
                    $"UPDATE relay_statistics SET {column} = {column} + $amount WHERE singleton = 1;";
                command.Parameters.AddWithValue("$amount", amount);
                if (command.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("Relay statistics storage is unavailable.");
                updateMemory(this, amount);
            }
            catch (Exception exception)
            {
                // Statistics must never interrupt room, transfer, or playback operations.
                logger.LogError(exception, "Could not persist anonymous relay statistic {Statistic}", column);
            }
            return SnapshotUnsafe();
        }
    }

    private RelayStatisticsDto SnapshotUnsafe() => new(
        Volatile.Read(ref activeUsers), roomsGenerated, sharedAnimations, animationsPerformed);
}

public sealed record RelayStatisticsDto(
    int ActiveUsers,
    long RoomsGenerated,
    long SharedAnimations,
    long AnimationsPerformed);
