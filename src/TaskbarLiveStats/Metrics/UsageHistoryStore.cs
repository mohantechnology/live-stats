using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace TaskbarLiveStats.Metrics;

internal enum UsageRange
{
    Minutes15,
    Hour,
    Hours5,
    Day,
    Week,
    Month,
    Months3,
    Months6,
    Year,
    Custom
}

internal readonly record struct UsageWindow(DateTime LocalStart, DateTime LocalEnd, int BucketSeconds);

internal readonly record struct UsageBucket(DateTime LocalStart, ulong NetSend, ulong NetRecv, ulong DiskRead, ulong DiskWrite);

internal readonly record struct UptimeBucket(DateTime LocalStart, int AwakeSeconds);

/// <summary>
/// Per-minute disk/network bytes and awake-time seconds in %AppData%\TaskbarLiveStats\usage.db.
/// Minutes the app did not record are absent and chart as zero.
/// </summary>
internal sealed class UsageHistoryStore : IDisposable
{
    public const int RetentionDays = 400;
    private const long FlushIntervalMs = 15_000;
    private const int MaxSecondsPerMinute = 60;

    private readonly string _path;
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();
    private readonly System.Threading.Timer _uptimeTimer;
    private bool _recordNetwork = true;
    private bool _recordDisk = true;
    private bool _recordComputerUse = true;
    private bool _suspended;
    private long _openMinute = -1;
    private ulong _pendingSend;
    private ulong _pendingRecv;
    private ulong _pendingRead;
    private ulong _pendingWrite;
    private long _uptimeOpenMinute = -1;
    private int _pendingAwakeSeconds;
    private long _accrueBaselineTick = -1;
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
        Execute("""
            CREATE TABLE IF NOT EXISTS uptime_minute (
                minute_utc INTEGER PRIMARY KEY,
                awake_seconds INTEGER NOT NULL
            );
            """);
        long cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)RetentionDays * 24 * 60 * 60;
        using (var prune = _connection.CreateCommand())
        {
            prune.CommandText = "DELETE FROM usage_minute WHERE minute_utc < $cutoff";
            prune.Parameters.AddWithValue("$cutoff", cutoff);
            prune.ExecuteNonQuery();
        }

        using (var pruneUptime = _connection.CreateCommand())
        {
            pruneUptime.CommandText = "DELETE FROM uptime_minute WHERE minute_utc < $cutoff";
            pruneUptime.Parameters.AddWithValue("$cutoff", cutoff);
            pruneUptime.ExecuteNonQuery();
        }

        _uptimeTimer = new System.Threading.Timer(_ => AccrueUptime(), null, FlushIntervalMs, FlushIntervalMs);
        try
        {
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
        }
        catch
        {
            // Accrual still runs from the timer while the process is awake.
        }

        lock (_gate)
            _accrueBaselineTick = Environment.TickCount64;
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

    public bool RecordComputerUse
    {
        get { lock (_gate) return _recordComputerUse; }
    }

    public void SetRecording(bool network, bool disk, bool computerUse)
    {
        lock (_gate)
        {
            bool turningOffBytes = (_recordNetwork && !network) || (_recordDisk && !disk);
            bool turningOffUptime = _recordComputerUse && !computerUse;
            bool turningOnUptime = !_recordComputerUse && computerUse;
            if (turningOffBytes || turningOffUptime)
                FlushLocked();
            _recordNetwork = network;
            _recordDisk = disk;
            _recordComputerUse = computerUse;
            if (turningOffUptime)
            {
                _accrueBaselineTick = -1;
                _uptimeOpenMinute = -1;
                _pendingAwakeSeconds = 0;
            }
            else if (turningOnUptime && !_suspended)
            {
                _accrueBaselineTick = Environment.TickCount64;
            }
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
                    FlushBytesLocked();
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
        try
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }
        catch
        {
            // Ignore unsubscribe failures during teardown.
        }

        _uptimeTimer.Dispose();
        lock (_gate)
        {
            if (_disposed)
                return;
            try
            {
                AccruePendingFromBaselineLocked();
                FlushLocked();
            }
            catch
            {
                // shutting down
            }

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

    public static bool HasAnyUptime(string path)
    {
        if (!File.Exists(path))
            return false;
        try
        {
            using var connection = OpenRead(path);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM uptime_minute LIMIT 1";
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
                start = AlignDown(now.AddHours(-1), 600);
                bucket = 600;
                break;
            case UsageRange.Hours5:
                start = AlignDown(now.AddHours(-5), 1800);
                bucket = 1800;
                break;
            case UsageRange.Week:
                // Today plus the previous 6 calendar days (7 daily bars).
                start = now.Date.AddDays(-6);
                bucket = 86_400;
                break;
            case UsageRange.Month:
                // Today plus the previous 29 calendar days (30 daily bars).
                start = now.Date.AddDays(-29);
                bucket = 86_400;
                break;
            case UsageRange.Months3:
                start = now.Date.AddDays(-89);
                bucket = 86_400;
                break;
            case UsageRange.Months6:
                start = now.Date.AddDays(-179);
                bucket = 86_400;
                break;
            case UsageRange.Year:
                start = now.Date.AddDays(-364);
                bucket = 86_400;
                break;
            case UsageRange.Day:
                // Full calendar day so 2-hour bars stay visible even early morning.
                start = now.Date;
                end = start.AddDays(1);
                bucket = 7200;
                break;
            case UsageRange.Custom:
                DateTime from = (customFrom ?? now.Date.AddDays(-7)).Date;
                DateTime to = (customTo ?? now.Date).Date;
                if (to < from)
                    (from, to) = (to, from);
                // Inclusive end date → exclusive next midnight (full last day, not "now").
                end = to.Date.AddDays(1);
                if (end - from > TimeSpan.FromDays(RetentionDays))
                    from = end.AddDays(-RetentionDays);
                start = from;
                double days = Math.Max(0, (end - start).TotalDays);
                bucket = days <= 1 ? 7200 : 86_400;
                break;
            default:
                start = now.Date;
                end = start.AddDays(1);
                bucket = 7200;
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
            long startUnix = ToUnix(window.LocalStart);
            long endUnix = ToUnix(window.LocalEnd);
            // Bucket each stored minute in local time so Day/Week/Month totals match the
            // raw minute sum (fixed SQL UTC offset drifts across DST and can drop bars).
            var totals = new Dictionary<long, UsageBucket>();
            using (var connection = OpenRead(path))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT minute_utc, net_send, net_recv, disk_read, disk_write
                    FROM usage_minute
                    WHERE minute_utc >= $start AND minute_utc < $end
                    """;
                command.Parameters.AddWithValue("$start", startUnix);
                command.Parameters.AddWithValue("$end", endUnix);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    DateTime bucketStart = AlignDown(FromUnix(reader.GetInt64(0)), window.BucketSeconds);
                    if (bucketStart < window.LocalStart || bucketStart >= window.LocalEnd)
                        continue;
                    long key = ToUnix(bucketStart);
                    ulong send = ReadUlong(reader, 1);
                    ulong recv = ReadUlong(reader, 2);
                    ulong read = ReadUlong(reader, 3);
                    ulong write = ReadUlong(reader, 4);
                    if (totals.TryGetValue(key, out var existing))
                    {
                        totals[key] = existing with
                        {
                            NetSend = existing.NetSend + send,
                            NetRecv = existing.NetRecv + recv,
                            DiskRead = existing.DiskRead + read,
                            DiskWrite = existing.DiskWrite + write
                        };
                    }
                    else
                    {
                        totals[key] = new UsageBucket(bucketStart, send, recv, read, write);
                    }
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
                cursor = NextBucketStart(cursor, window.BucketSeconds);
            }

            return filled.ToArray();
        }
        catch
        {
            return [];
        }
    }

    public static UptimeBucket[] ReadUptime(string path, UsageWindow window)
    {
        if (!File.Exists(path) || window.BucketSeconds <= 0 || window.LocalEnd <= window.LocalStart)
            return [];

        try
        {
            long startUnix = ToUnix(window.LocalStart);
            long endUnix = ToUnix(window.LocalEnd);
            var totals = new Dictionary<long, int>();
            using (var connection = OpenRead(path))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT minute_utc, awake_seconds
                    FROM uptime_minute
                    WHERE minute_utc >= $start AND minute_utc < $end
                    """;
                command.Parameters.AddWithValue("$start", startUnix);
                command.Parameters.AddWithValue("$end", endUnix);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    DateTime bucketStart = AlignDown(FromUnix(reader.GetInt64(0)), window.BucketSeconds);
                    if (bucketStart < window.LocalStart || bucketStart >= window.LocalEnd)
                        continue;
                    long key = ToUnix(bucketStart);
                    int seconds = reader.IsDBNull(1) ? 0 : (int)Math.Clamp(reader.GetInt64(1), 0, int.MaxValue);
                    totals[key] = totals.TryGetValue(key, out int existing)
                        ? (int)Math.Min((long)existing + seconds, int.MaxValue)
                        : seconds;
                }
            }

            var filled = new List<UptimeBucket>();
            DateTime cursor = AlignDown(window.LocalStart, window.BucketSeconds);
            while (cursor < window.LocalEnd)
            {
                long key = ToUnix(cursor);
                filled.Add(new UptimeBucket(cursor, totals.TryGetValue(key, out int seconds) ? seconds : 0));
                cursor = NextBucketStart(cursor, window.BucketSeconds);
            }

            return filled.ToArray();
        }
        catch
        {
            return [];
        }
    }

    private void AccrueUptime()
    {
        try
        {
            lock (_gate)
            {
                if (_disposed || !_recordComputerUse || _suspended)
                    return;
                long nowTick = Environment.TickCount64;
                if (_accrueBaselineTick < 0)
                {
                    _accrueBaselineTick = nowTick;
                    return;
                }

                long elapsedMs = nowTick - _accrueBaselineTick;
                if (elapsedMs < 1000)
                    return;
                int seconds = (int)Math.Min(elapsedMs / 1000, MaxSecondsPerMinute * 2);
                _accrueBaselineTick = nowTick - (elapsedMs % 1000);
                AddUptimeSecondsLocked(DateTime.UtcNow, seconds);
                if (Environment.TickCount64 - _lastFlushTick >= FlushIntervalMs)
                    FlushLocked();
            }
        }
        catch
        {
            // Keep accruing on the next tick.
        }
    }

    private void AddUptimeSecondsLocked(DateTime utc, int seconds)
    {
        if (seconds <= 0)
            return;
        long minute = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds() / 60 * 60;
        if (_uptimeOpenMinute >= 0 && minute != _uptimeOpenMinute)
            FlushUptimeLocked();
        _uptimeOpenMinute = minute;
        _pendingAwakeSeconds = Math.Min(MaxSecondsPerMinute, _pendingAwakeSeconds + seconds);
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                if (e.Mode == PowerModes.Suspend)
                {
                    AccruePendingFromBaselineLocked();
                    FlushLocked();
                    _suspended = true;
                    _accrueBaselineTick = -1;
                }
                else if (e.Mode == PowerModes.Resume)
                {
                    _suspended = false;
                    if (_recordComputerUse)
                        _accrueBaselineTick = Environment.TickCount64;
                }
            }
        }
        catch
        {
            // Ignore power-event failures; the next timer tick recovers.
        }
    }

    private void AccruePendingFromBaselineLocked()
    {
        if (!_recordComputerUse || _suspended || _accrueBaselineTick < 0)
            return;
        long elapsedMs = Environment.TickCount64 - _accrueBaselineTick;
        if (elapsedMs < 1000)
            return;
        int seconds = (int)Math.Min(elapsedMs / 1000, MaxSecondsPerMinute * 2);
        _accrueBaselineTick = -1;
        AddUptimeSecondsLocked(DateTime.UtcNow, seconds);
    }

    private void FlushLocked()
    {
        FlushBytesLocked();
        FlushUptimeLocked();
        _lastFlushTick = Environment.TickCount64;
    }

    private void FlushBytesLocked()
    {
        if (_openMinute < 0)
            return;
        if (_pendingSend == 0 && _pendingRecv == 0 && _pendingRead == 0 && _pendingWrite == 0)
            return;

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
    }

    private void FlushUptimeLocked()
    {
        if (_uptimeOpenMinute < 0 || _pendingAwakeSeconds <= 0)
            return;

        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO uptime_minute (minute_utc, awake_seconds)
            VALUES ($minute, $seconds)
            ON CONFLICT(minute_utc) DO UPDATE SET
                awake_seconds = MIN(60, awake_seconds + excluded.awake_seconds)
            """;
        command.Parameters.AddWithValue("$minute", _uptimeOpenMinute);
        command.Parameters.AddWithValue("$seconds", Math.Clamp(_pendingAwakeSeconds, 0, MaxSecondsPerMinute));
        command.ExecuteNonQuery();
        _pendingAwakeSeconds = 0;
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
        if (bucketSeconds == 86_400)
            return DateTime.SpecifyKind(local.Date, DateTimeKind.Unspecified);

        long unix = ToUnix(local);
        int offset = (int)TimeZoneInfo.Local.GetUtcOffset(local).TotalSeconds;
        long shifted = unix + offset;
        long aligned = shifted >= 0
            ? shifted / bucketSeconds * bucketSeconds
            : (long)Math.Floor(shifted / (double)bucketSeconds) * bucketSeconds;
        return FromUnix(aligned - offset);
    }

    /// <summary>Advance one chart bar. Daily bars follow calendar midnights.</summary>
    private static DateTime NextBucketStart(DateTime cursor, int bucketSeconds)
    {
        DateTime next = bucketSeconds == 86_400
            ? cursor.Date.AddDays(1)
            : cursor.AddSeconds(bucketSeconds);
        return next > cursor ? next : cursor.AddMinutes(1);
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
