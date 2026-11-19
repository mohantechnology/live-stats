using Microsoft.Data.Sqlite;

namespace TaskbarLiveStats.Metrics;

internal enum UsageRange
{
    Minutes15,
    Hour,
    Hours5,
    Day,
    Week,
    Month,
    Year,
    Custom
}

internal readonly record struct UsageWindow(DateTime LocalStart, DateTime LocalEnd, int BucketSeconds);

internal readonly record struct UsageBucket(DateTime LocalStart, ulong NetSend, ulong NetRecv, ulong DiskRead, ulong DiskWrite);

/// <summary>
/// Per-minute disk and network byte totals in %AppData%\TaskbarLiveStats\usage.db.
/// Minutes the app did not record are absent and chart as zero.
/// </summary>
internal sealed class UsageHistoryStore : IDisposable
{
    public const int RetentionDays = 400;
    private const long FlushIntervalMs = 15_000;

    private readonly string _path;
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();
    private bool _recordNetwork = true;
    private bool _recordDisk = true;
    private long _openMinute = -1;
    private ulong _pendingSend;
    private ulong _pendingRecv;
    private ulong _pendingRead;
    private ulong _pendingWrite;
    private long _lastFlushTick = Environment.TickCount64;
    private bool _disposed;

    public UsageHistoryStore(string? path = null)
    {
        _path = path ?? DefaultPath;
        string? dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };
        _connection = new SqliteConnection(builder.ToString());
        _connection.Open();
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA busy_timeout=2000;");
        Execute("PRAGMA synchronous=NORMAL;");
        Execute("""
            CREATE TABLE IF NOT EXISTS usage_minute (
                minute_utc INTEGER PRIMARY KEY,
                net_send INTEGER NOT NULL,
                net_recv INTEGER NOT NULL,
                disk_read INTEGER NOT NULL,
                disk_write INTEGER NOT NULL
            );
            """);
        long cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)RetentionDays * 24 * 60 * 60;
        using var prune = _connection.CreateCommand();
        prune.CommandText = "DELETE FROM usage_minute WHERE minute_utc < $cutoff";
        prune.Parameters.AddWithValue("$cutoff", cutoff);
        prune.ExecuteNonQuery();
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TaskbarLiveStats",
        "usage.db");

    public bool RecordNetwork
    {
        get { lock (_gate) return _recordNetwork; }
    }

    public bool RecordDisk
    {
        get { lock (_gate) return _recordDisk; }
    }

    public void SetRecording(bool network, bool disk)
    {
        lock (_gate)
        {
            bool turningOff = (_recordNetwork && !network) || (_recordDisk && !disk);
            if (turningOff)
                FlushLocked();
            _recordNetwork = network;
            _recordDisk = disk;
        }
    }

    public void Add(DateTime utc, ulong netSend, ulong netRecv, ulong diskRead, ulong diskWrite)
    {
        try
        {
            long minute = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds() / 60 * 60;
            lock (_gate)
            {
                if (_disposed || (!_recordNetwork && !_recordDisk))
                    return;
                if (!_recordNetwork)
                {
                    netSend = 0;
                    netRecv = 0;
                }

                if (!_recordDisk)
                {
                    diskRead = 0;
                    diskWrite = 0;
                }

                // Flush the previous minute before the new deltas land on it.
                if (_openMinute >= 0 && minute != _openMinute)
                    FlushLocked();
                if (netSend == 0 && netRecv == 0 && diskRead == 0 && diskWrite == 0)
                    return;
                _openMinute = minute;
                _pendingSend += netSend;
                _pendingRecv += netRecv;
                _pendingRead += diskRead;
                _pendingWrite += diskWrite;
                if (Environment.TickCount64 - _lastFlushTick >= FlushIntervalMs)
                    FlushLocked();
            }
        }
        catch
        {
            // A full disk or a locked file must not stop live sampling.
        }
    }

    public void Flush()
    {
        try
        {
            lock (_gate)
            {
                if (!_disposed)
                    FlushLocked();
            }
        }
        catch
        {
            // Keep the in-memory minute for the next flush.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            try { FlushLocked(); } catch { /* shutting down */ }
            _disposed = true;
            _connection.Dispose();
        }
    }

    public static bool HasAny(string path)
    {
        if (!File.Exists(path))
            return false;
        try
        {
            using var connection = OpenRead(path);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM usage_minute LIMIT 1";
            return command.ExecuteScalar() != null;
        }
        catch
        {
            return false;
        }
    }

    public static UsageWindow Resolve(UsageRange range, DateTime nowLocal, DateTime? customFrom, DateTime? customTo)
    {
        DateTime now = DateTime.SpecifyKind(nowLocal, DateTimeKind.Unspecified);
        DateTime start;
        DateTime end = now;
        int bucket;
        switch (range)
        {
            case UsageRange.Minutes15:
                start = AlignDown(now.AddMinutes(-15), 60);
                bucket = 60;
                break;
            case UsageRange.Hour:
                start = AlignDown(now.AddHours(-1), 60);
                bucket = 60;
                break;
            case UsageRange.Hours5:
                start = AlignDown(now.AddHours(-5), 300);
                bucket = 300;
                break;
            case UsageRange.Week:
                start = now.Date.AddDays(-7);
                bucket = 86_400;
                break;
            case UsageRange.Month:
                start = now.Date.AddDays(-30);
                bucket = 86_400;
                break;
            case UsageRange.Year:
                start = now.Date.AddDays(-365);
                bucket = 86_400;
                break;
            case UsageRange.Day:
                start = now.Date;
                bucket = 900;
                break;
            case UsageRange.Custom:
                DateTime from = (customFrom ?? now.Date.AddDays(-7)).Date;
                DateTime to = (customTo ?? now.Date).Date;
                if (to < from)
                    (from, to) = (to, from);
                end = to.Date >= now.Date ? now : to.Date.AddDays(1);
                if (end - from > TimeSpan.FromDays(RetentionDays))
                    from = end.AddDays(-RetentionDays);
                start = from;
                double days = Math.Max(0, (end - start).TotalDays);
                bucket = days <= 1 ? 900 : 86_400;
                break;
            default:
                start = now.Date;
                bucket = 900;
                break;
        }

        if (end <= start)
            end = start.AddSeconds(Math.Max(bucket, 1));
        return new UsageWindow(start, end, bucket);
    }

    public static UsageBucket[] Read(string path, UsageWindow window)
    {
        if (!File.Exists(path) || window.BucketSeconds <= 0 || window.LocalEnd <= window.LocalStart)
            return [];

        try
        {
            int offset = (int)TimeZoneInfo.Local.GetUtcOffset(window.LocalStart).TotalSeconds;
            long startUnix = ToUnix(window.LocalStart);
            long endUnix = ToUnix(window.LocalEnd);
            var totals = new Dictionary<long, UsageBucket>();
            using (var connection = OpenRead(path))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT ((minute_utc + $off) / $bucket) * $bucket - $off AS bucket_utc,
                           SUM(net_send), SUM(net_recv), SUM(disk_read), SUM(disk_write)
                    FROM usage_minute
                    WHERE minute_utc >= $start AND minute_utc < $end
                    GROUP BY bucket_utc
                    ORDER BY bucket_utc
                    """;
                command.Parameters.AddWithValue("$off", offset);
                command.Parameters.AddWithValue("$bucket", window.BucketSeconds);
                command.Parameters.AddWithValue("$start", startUnix);
                command.Parameters.AddWithValue("$end", endUnix);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    long bucketUtc = reader.GetInt64(0);
                    totals[bucketUtc] = new UsageBucket(
                        FromUnix(bucketUtc),
                        ReadUlong(reader, 1),
                        ReadUlong(reader, 2),
                        ReadUlong(reader, 3),
                        ReadUlong(reader, 4));
                }
            }

            var filled = new List<UsageBucket>();
            DateTime cursor = AlignDown(window.LocalStart, window.BucketSeconds);
            while (cursor < window.LocalEnd)
            {
                long key = ToUnix(cursor);
                filled.Add(totals.TryGetValue(key, out var hit)
                    ? hit with { LocalStart = cursor }
                    : new UsageBucket(cursor, 0, 0, 0, 0));
                cursor = cursor.AddSeconds(window.BucketSeconds);
            }

            return filled.ToArray();
        }
        catch
        {
            return [];
        }
    }

    private void FlushLocked()
    {
        if (_openMinute < 0)
            return;
        if (_pendingSend == 0 && _pendingRecv == 0 && _pendingRead == 0 && _pendingWrite == 0)
        {
            _lastFlushTick = Environment.TickCount64;
            return;
        }

        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO usage_minute (minute_utc, net_send, net_recv, disk_read, disk_write)
            VALUES ($minute, $send, $recv, $read, $write)
            ON CONFLICT(minute_utc) DO UPDATE SET
                net_send = net_send + excluded.net_send,
                net_recv = net_recv + excluded.net_recv,
                disk_read = disk_read + excluded.disk_read,
                disk_write = disk_write + excluded.disk_write
            """;
        command.Parameters.AddWithValue("$minute", _openMinute);
        command.Parameters.AddWithValue("$send", Clamp(_pendingSend));
        command.Parameters.AddWithValue("$recv", Clamp(_pendingRecv));
        command.Parameters.AddWithValue("$read", Clamp(_pendingRead));
        command.Parameters.AddWithValue("$write", Clamp(_pendingWrite));
        command.ExecuteNonQuery();
        _pendingSend = _pendingRecv = _pendingRead = _pendingWrite = 0;
        _lastFlushTick = Environment.TickCount64;
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static SqliteConnection OpenRead(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=2000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static ulong ReadUlong(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return 0;
        long value = reader.GetInt64(ordinal);
        return value <= 0 ? 0 : (ulong)value;
    }

    private static long Clamp(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;

    private static DateTime AlignDown(DateTime local, int bucketSeconds)
    {
        long unix = ToUnix(local);
        int offset = (int)TimeZoneInfo.Local.GetUtcOffset(local).TotalSeconds;
        long shifted = unix + offset;
        long aligned = shifted >= 0
            ? shifted / bucketSeconds * bucketSeconds
            : (long)Math.Floor(shifted / (double)bucketSeconds) * bucketSeconds;
        return FromUnix(aligned - offset);
    }

    private static long ToUnix(DateTime local)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        var utc = TimeZoneInfo.ConvertTimeToUtc(unspecified, TimeZoneInfo.Local);
        return new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
    }

    private static DateTime FromUnix(long unix)
    {
        var utc = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local), DateTimeKind.Unspecified);
    }
}
