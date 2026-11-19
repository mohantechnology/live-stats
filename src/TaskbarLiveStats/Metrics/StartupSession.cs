using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace TaskbarLiveStats.Metrics;

internal enum SessionEdge
{
    Same,
    Shutdown,
    Restart,
    Hibernate,
    Sleep
}

internal readonly record struct ByteTotals(ulong Send, ulong Recv, ulong Read, ulong Write)
{
    public static ByteTotals Zero { get; } = new(0, 0, 0, 0);

    public static ulong SessionBytes(ulong carry, ulong current, ulong origin)
    {
        ulong net = current >= origin ? current - origin : 0;
        return Add(carry, net);
    }

    private static ulong Add(ulong a, ulong b)
    {
        ulong sum = a + b;
        return sum < a ? ulong.MaxValue : sum;
    }
}

/// <summary>
/// One startup session. Display is carry + current counter - origin, never below zero.
/// </summary>
internal readonly record struct SessionLedger(
    long BootUtc,
    long ShutdownFileTime,
    ByteTotals Carry,
    ByteTotals Origin,
    ByteTotals Last)
{
    public ByteTotals Display(ByteTotals current) => new(
        ByteTotals.SessionBytes(Carry.Send, current.Send, Origin.Send),
        ByteTotals.SessionBytes(Carry.Recv, current.Recv, Origin.Recv),
        ByteTotals.SessionBytes(Carry.Read, current.Read, Origin.Read),
        ByteTotals.SessionBytes(Carry.Write, current.Write, Origin.Write));
}

/// <summary>
/// Pure session transitions. Shutdown and restart start a new session.
/// Hibernate keeps it, carrying the old total forward if the OS counters were reset.
/// Sleep, logout, and lock stay on <see cref="SessionEdge.Same"/> and keep the total.
/// </summary>
internal static class SessionMath
{
    public static SessionLedger Cross(
        SessionLedger saved,
        SessionEdge edge,
        bool countersReset,
        long bootUtc,
        long shutdownFileTime,
        ByteTotals current)
    {
        switch (edge)
        {
            case SessionEdge.Hibernate:
                if (!countersReset)
                    return saved with { BootUtc = bootUtc, ShutdownFileTime = shutdownFileTime, Last = current };
                return new SessionLedger(bootUtc, shutdownFileTime, saved.Display(saved.Last), ByteTotals.Zero, current);
            case SessionEdge.Shutdown:
            case SessionEdge.Restart:
                ByteTotals origin = countersReset ? ByteTotals.Zero : saved.Last;
                return new SessionLedger(bootUtc, shutdownFileTime, ByteTotals.Zero, origin, current);
            default:
                return saved with { Last = current };
        }
    }

    public static SessionEdge Classify(
        long savedBootUtc,
        long savedShutdown,
        long bootUtc,
        long shutdownFileTime,
        SessionEdge logged)
    {
        if (savedBootUtc != 0 && bootUtc != 0 && bootUtc != savedBootUtc)
        {
            return logged switch
            {
                SessionEdge.Hibernate => SessionEdge.Hibernate,
                SessionEdge.Shutdown => SessionEdge.Shutdown,
                _ => SessionEdge.Restart
            };
        }

        if (savedShutdown != 0 && shutdownFileTime > savedShutdown)
            return SessionEdge.Shutdown;

        return SessionEdge.Same;
    }

    public static SessionEdge Fold(IReadOnlyList<SessionEdge> chronological)
    {
        bool cleared = false;
        SessionEdge clearEdge = SessionEdge.Restart;
        bool sawHibernate = false;
        foreach (SessionEdge edge in chronological)
        {
            switch (edge)
            {
                case SessionEdge.Restart:
                    cleared = true;
                    clearEdge = SessionEdge.Restart;
                    sawHibernate = false;
                    break;
                case SessionEdge.Shutdown:
                    cleared = true;
                    clearEdge = SessionEdge.Shutdown;
                    sawHibernate = false;
                    break;
                case SessionEdge.Hibernate:
                    sawHibernate = true;
                    break;
            }
        }

        if (sawHibernate && !cleared)
            return SessionEdge.Hibernate;
        if (cleared)
            return clearEdge;
        return SessionEdge.Same;
    }

    /// <summary>True when every sizable counter fell by more than half, as on a fresh boot.</summary>
    public static bool CountersReset(ByteTotals last, ByteTotals current)
    {
        int comparable = 0;
        int dropped = 0;
        Note(last.Send, current.Send, ref comparable, ref dropped);
        Note(last.Recv, current.Recv, ref comparable, ref dropped);
        Note(last.Read, current.Read, ref comparable, ref dropped);
        Note(last.Write, current.Write, ref comparable, ref dropped);
        return comparable > 0 && dropped == comparable;
    }

    /// <summary>
    /// When a single channel falls below its session origin (adapter reset or a
    /// corrected counter source), keep any carry and follow the new reading.
    /// Without this, Startup totals stay stuck at 0 forever for that channel.
    /// </summary>
    public static SessionLedger RebaseRegressed(SessionLedger state, ByteTotals current)
    {
        ulong oSend = state.Origin.Send, oRecv = state.Origin.Recv, oRead = state.Origin.Read, oWrite = state.Origin.Write;
        ulong cSend = state.Carry.Send, cRecv = state.Carry.Recv, cRead = state.Carry.Read, cWrite = state.Carry.Write;
        bool changed = false;
        Fix(current.Send, state.Last.Send, ref oSend, ref cSend, ref changed);
        Fix(current.Recv, state.Last.Recv, ref oRecv, ref cRecv, ref changed);
        Fix(current.Read, state.Last.Read, ref oRead, ref cRead, ref changed);
        Fix(current.Write, state.Last.Write, ref oWrite, ref cWrite, ref changed);
        if (!changed)
            return state;
        return state with
        {
            Origin = new ByteTotals(oSend, oRecv, oRead, oWrite),
            Carry = new ByteTotals(cSend, cRecv, cRead, cWrite)
        };
    }

    private static void Fix(ulong current, ulong last, ref ulong origin, ref ulong carry, ref bool changed)
    {
        if (current >= origin)
            return;

        ulong prior = ByteTotals.SessionBytes(carry, last, origin);
        if (prior == 0 && last < origin)
        {
            // Origin is on an incompatible scale (for example an old inflated sum).
            // Follow the live since-boot counter so the total is not stuck at zero.
            carry = 0;
            origin = 0;
        }
        else
        {
            carry = prior;
            origin = current;
        }

        changed = true;
    }

    private static void Note(ulong last, ulong current, ref int comparable, ref int dropped)
    {
        if (last < 4096)
            return;
        comparable++;
        if (current < last && last - current > last / 2)
            dropped++;
    }
}

/// <summary>Boot clock and the last Windows shutdown time.</summary>
internal static class BootClock
{
    private static long _fallbackBoot;

    public static long Stamp()
    {
        long native = QueryBootFileTime();
        if (native != 0)
            return native;
        if (_fallbackBoot == 0)
        {
            long elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64).Ticks;
            _fallbackBoot = DateTime.UtcNow.Ticks - elapsed;
            if (_fallbackBoot == 0)
                _fallbackBoot = 1;
        }

        return _fallbackBoot;
    }

    public static long ShutdownFileTime()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Windows");
            if (key?.GetValue("ShutdownTime") is byte[] raw && raw.Length >= 8)
                return BitConverter.ToInt64(raw, 0);
        }
        catch
        {
            // A missing shutdown time cannot start a new session by itself.
        }

        return 0;
    }

    private static long QueryBootFileTime()
    {
        try
        {
            var info = new SystemTimeOfDay();
            int status = NtQuerySystemInformation(3, ref info, Marshal.SizeOf<SystemTimeOfDay>(), IntPtr.Zero);
            return status == 0 && info.BootTime > 0 ? info.BootTime : 0;
        }
        catch
        {
            return 0;
        }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int systemInformationClass,
        ref SystemTimeOfDay systemInformation,
        int systemInformationLength,
        IntPtr returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTimeOfDay
    {
        public long BootTime;
        public long CurrentTime;
        public long TimeZoneBias;
        public uint TimeZoneId;
        public uint Reserved;
        public ulong BootTimeBias;
        public ulong SleepTimeBias;
    }
}

internal static class BootEvents
{
    public static SessionEdge FoldSince(DateTime sinceUtc)
    {
        var found = new List<(DateTime Utc, SessionEdge Edge)>();
        Collect("System", 1074, 41, 6008, 42, sinceUtc, found);
        Collect("Microsoft-Windows-Kernel-Boot/Operational", 27, 0, 0, 0, sinceUtc, found);
        found.Sort(static (x, y) => x.Utc.CompareTo(y.Utc));
        var edges = new List<SessionEdge>(found.Count);
        foreach ((DateTime _, SessionEdge edge) in found)
            edges.Add(edge);
        return SessionMath.Fold(edges);
    }

    private static void Collect(string log, int a, int b, int c, int d, DateTime sinceUtc, List<(DateTime Utc, SessionEdge Edge)> found)
    {
        try
        {
            string ids = d == 0 ? $"EventID={a}" : $"EventID={a} or EventID={b} or EventID={c} or EventID={d}";
            string xpath = $"*[System[({ids}) and TimeCreated[timediff(@SystemTime) <= 2592000000]]]";
            var query = new EventLogQuery(log, PathType.LogName, xpath);
            using var reader = new EventLogReader(query);
            for (EventRecord? record = reader.ReadEvent(); record != null; record = reader.ReadEvent())
            {
                using (record)
                {
                    if (record.TimeCreated is not DateTime created)
                        continue;
                    DateTime utc = ToUtc(created);
                    if (utc < sinceUtc)
                        continue;
                    SessionEdge edge = Map(record.Id, record.ToXml());
                    if (edge != SessionEdge.Same)
                        found.Add((utc, edge));
                }
            }
        }
        catch
        {
            // An unreadable log leaves the boot unclassified, which starts a new session.
        }
    }

    private static SessionEdge Map(int id, string xml)
    {
        switch (id)
        {
            case 1074:
                string kind = Value(xml, "param5");
                if (kind.Length == 0)
                    kind = Nth(xml, 4);
                return MapKind(kind);
            case 41:
            case 6008:
                return SessionEdge.Shutdown;
            case 42:
                int target = Number(xml, "TargetState");
                if (target == 4)
                    return SessionEdge.Hibernate;
                if (target is >= 1 and <= 3)
                    return SessionEdge.Sleep;
                if (target == 5)
                    return SessionEdge.Shutdown;
                return SessionEdge.Same;
            case 27:
                int boot = Number(xml, "BootType");
                if (boot < 0)
                    boot = Parse(Nth(xml, 0));
                if (boot == 2)
                    return SessionEdge.Hibernate;
                if (boot == 1)
                    return SessionEdge.Shutdown;
                return SessionEdge.Same;
            default:
                return SessionEdge.Same;
        }
    }

    private static SessionEdge MapKind(string kind)
    {
        string text = kind.Trim().ToLowerInvariant();
        if (text.Contains("hibernate", StringComparison.Ordinal))
            return SessionEdge.Hibernate;
        if (text.Contains("restart", StringComparison.Ordinal))
            return SessionEdge.Restart;
        if (text.Contains("shutdown", StringComparison.Ordinal) || text.Contains("power off", StringComparison.Ordinal))
            return SessionEdge.Shutdown;
        return SessionEdge.Same;
    }

    private static int Number(string xml, string name)
    {
        string text = Value(xml, name);
        return text.Length == 0 ? -1 : Parse(text);
    }

    private static int Parse(string text)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hex))
            return hex;
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : -1;
    }

    private static string Value(string xml, string name)
    {
        string token = "Name=\"" + name + "\">";
        int i = xml.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (i < 0)
            return "";
        i += token.Length;
        int j = xml.IndexOf('<', i);
        return j > i ? xml.Substring(i, j - i).Trim() : "";
    }

    private static string Nth(string xml, int index)
    {
        int from = 0;
        for (int n = 0; n <= index; n++)
        {
            int start = xml.IndexOf("<Data", from, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return "";
            int open = xml.IndexOf('>', start);
            if (open < 0)
                return "";
            int close = xml.IndexOf('<', open + 1);
            if (close < 0)
                return "";
            if (n == index)
                return xml.Substring(open + 1, close - open - 1).Trim();
            from = close + 1;
        }

        return "";
    }

    private static DateTime ToUtc(DateTime time) => time.Kind switch
    {
        DateTimeKind.Utc => time,
        DateTimeKind.Local => time.ToUniversalTime(),
        _ => DateTime.SpecifyKind(time, DateTimeKind.Local).ToUniversalTime()
    };
}

/// <summary>
/// Persists the startup-session ledger in usage.db. The shown total is the bytes
/// transferred since the PC last came back from a shutdown or a restart.
/// </summary>
internal sealed class StartupSessionStore : IDisposable
{
    private const long FlushIntervalMs = 15_000;

    private readonly SqliteConnection _connection;
    private readonly object _gate = new();
    private SessionLedger? _state;
    private ByteTotals _latest;
    private DateTime _updatedUtc = DateTime.UnixEpoch;
    private long _lastTick;
    private bool _disposed;

    public StartupSessionStore(string? path = null)
    {
        string file = path ?? UsageHistoryStore.DefaultPath;
        string? dir = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = file,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };
        _connection = new SqliteConnection(builder.ToString());
        _connection.Open();
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA busy_timeout=2000;");
        Execute("""
            CREATE TABLE IF NOT EXISTS session_ledger (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                boot_utc INTEGER NOT NULL,
                shutdown_filetime INTEGER NOT NULL,
                carry_send INTEGER NOT NULL,
                carry_recv INTEGER NOT NULL,
                carry_read INTEGER NOT NULL,
                carry_write INTEGER NOT NULL,
                origin_send INTEGER NOT NULL,
                origin_recv INTEGER NOT NULL,
                origin_read INTEGER NOT NULL,
                origin_write INTEGER NOT NULL,
                last_send INTEGER NOT NULL,
                last_recv INTEGER NOT NULL,
                last_read INTEGER NOT NULL,
                last_write INTEGER NOT NULL,
                updated_utc INTEGER NOT NULL
            );
            """);
        _state = Load();
        try
        {
            SystemEvents.SessionEnding += OnSessionEnding;
            SystemEvents.PowerModeChanged += OnPowerMode;
        }
        catch
        {
            // The ledger still updates from the sample timer.
        }
    }

    public ByteTotals Display(ByteTotals current)
    {
        lock (_gate)
        {
            if (_state is null)
                return current;
            SessionLedger state = SessionMath.RebaseRegressed(_state.Value, current);
            _state = state;
            return state.Display(current);
        }
    }

    public void Observe(ByteTotals current, bool force)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _latest = current;
                long tick = Environment.TickCount64;
                long boot = BootClock.Stamp();
                long shutdown = BootClock.ShutdownFileTime();
                bool rebased = false;
                if (_state is SessionLedger open)
                {
                    SessionLedger next = SessionMath.RebaseRegressed(open, current);
                    rebased = next.Origin != open.Origin || next.Carry != open.Carry;
                    _state = next;
                }

                bool boundary = _state is SessionLedger saved && Boundary(saved, boot, shutdown);
                if (!force && !rebased && !boundary && _state != null && tick - _lastTick < FlushIntervalMs)
                    return;

                if (_state is null)
                {
                    _state = new SessionLedger(boot, shutdown, ByteTotals.Zero, ByteTotals.Zero, current);
                }
                else if (boundary)
                {
                    DateTime since = _updatedUtc <= DateTime.UnixEpoch
                        ? DateTime.UtcNow.AddDays(-30)
                        : _updatedUtc;
                    SessionEdge logged = BootEvents.FoldSince(since);
                    SessionLedger prior = _state.Value;
                    SessionEdge edge = SessionMath.Classify(prior.BootUtc, prior.ShutdownFileTime, boot, shutdown, logged);
                    bool reset = SessionMath.CountersReset(prior.Last, current);
                    _state = SessionMath.Cross(prior, edge, reset, boot, shutdown, current);
                }
                else
                {
                    _state = _state.Value with { Last = current };
                }

                _updatedUtc = DateTime.UtcNow;
                SaveLocked();
                _lastTick = tick;
            }
        }
        catch
        {
            // A locked database must not stop live sampling.
        }
    }

    public void Dispose()
    {
        SystemEvents.SessionEnding -= OnSessionEnding;
        SystemEvents.PowerModeChanged -= OnPowerMode;
        lock (_gate)
        {
            if (_disposed)
                return;
            try
            {
                if (_state != null)
                {
                    _state = _state.Value with { Last = _latest };
                    SaveLocked();
                }
            }
            catch
            {
                // Shutting down.
            }

            _disposed = true;
            _connection.Dispose();
        }
    }

    private void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        lock (_gate)
        {
            if (_disposed || _state is null)
                return;
            try
            {
                _state = _state.Value with { Last = _latest };
                _updatedUtc = DateTime.UtcNow;
                SaveLocked();
            }
            catch
            {
                // The next boot still has the last flushed sample.
            }
        }
    }

    private void OnPowerMode(object sender, PowerModeChangedEventArgs e)
    {
        // Resume rechecks hibernate. Sleep itself does not start a new session.
        if (e.Mode == PowerModes.Resume)
            Observe(_latest, force: true);
    }

    private static bool Boundary(SessionLedger saved, long boot, long shutdown) =>
        (saved.BootUtc != 0 && boot != 0 && boot != saved.BootUtc)
        || (saved.ShutdownFileTime != 0 && shutdown > saved.ShutdownFileTime);

    private SessionLedger? Load()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT boot_utc, shutdown_filetime,
                   carry_send, carry_recv, carry_read, carry_write,
                   origin_send, origin_recv, origin_read, origin_write,
                   last_send, last_recv, last_read, last_write, updated_utc
            FROM session_ledger WHERE id = 1
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        long updated = reader.GetInt64(14);
        if (updated > 0)
            _updatedUtc = DateTimeOffset.FromUnixTimeSeconds(updated).UtcDateTime;
        return new SessionLedger(
            reader.GetInt64(0),
            reader.GetInt64(1),
            new ByteTotals(ReadUlong(reader, 2), ReadUlong(reader, 3), ReadUlong(reader, 4), ReadUlong(reader, 5)),
            new ByteTotals(ReadUlong(reader, 6), ReadUlong(reader, 7), ReadUlong(reader, 8), ReadUlong(reader, 9)),
            new ByteTotals(ReadUlong(reader, 10), ReadUlong(reader, 11), ReadUlong(reader, 12), ReadUlong(reader, 13)));
    }

    private void SaveLocked()
    {
        if (_state is not SessionLedger state)
            return;
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO session_ledger (
                id, boot_utc, shutdown_filetime,
                carry_send, carry_recv, carry_read, carry_write,
                origin_send, origin_recv, origin_read, origin_write,
                last_send, last_recv, last_read, last_write, updated_utc)
            VALUES (1, $boot, $shutdown, $cs, $cr, $cd, $cw, $os, $or, $od, $ow, $ls, $lr, $ld, $lw, $updated)
            ON CONFLICT(id) DO UPDATE SET
                boot_utc = excluded.boot_utc,
                shutdown_filetime = excluded.shutdown_filetime,
                carry_send = excluded.carry_send,
                carry_recv = excluded.carry_recv,
                carry_read = excluded.carry_read,
                carry_write = excluded.carry_write,
                origin_send = excluded.origin_send,
                origin_recv = excluded.origin_recv,
                origin_read = excluded.origin_read,
                origin_write = excluded.origin_write,
                last_send = excluded.last_send,
                last_recv = excluded.last_recv,
                last_read = excluded.last_read,
                last_write = excluded.last_write,
                updated_utc = excluded.updated_utc
            """;
        command.Parameters.AddWithValue("$boot", state.BootUtc);
        command.Parameters.AddWithValue("$shutdown", state.ShutdownFileTime);
        Add(command, "$cs", state.Carry.Send);
        Add(command, "$cr", state.Carry.Recv);
        Add(command, "$cd", state.Carry.Read);
        Add(command, "$cw", state.Carry.Write);
        Add(command, "$os", state.Origin.Send);
        Add(command, "$or", state.Origin.Recv);
        Add(command, "$od", state.Origin.Read);
        Add(command, "$ow", state.Origin.Write);
        Add(command, "$ls", state.Last.Send);
        Add(command, "$lr", state.Last.Recv);
        Add(command, "$ld", state.Last.Read);
        Add(command, "$lw", state.Last.Write);
        long unix = _updatedUtc <= DateTime.UnixEpoch ? 0 : new DateTimeOffset(_updatedUtc).ToUnixTimeSeconds();
        command.Parameters.AddWithValue("$updated", unix);
        command.ExecuteNonQuery();
    }

    private static void Add(SqliteCommand command, string name, ulong value)
    {
        long stored = value > long.MaxValue ? long.MaxValue : (long)value;
        command.Parameters.AddWithValue(name, stored);
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static ulong ReadUlong(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return 0;
        long value = reader.GetInt64(ordinal);
        return value <= 0 ? 0 : (ulong)value;
    }
}
