using Microsoft.Data.Sqlite;
using PomodoroClock.Core;

namespace PomodoroClock.Infrastructure;

public sealed class SqliteDatabase
{
    public string Path { get; }
    public string ConnectionString => new SqliteConnectionStringBuilder { DataSource = Path, Mode = SqliteOpenMode.ReadWriteCreate, ForeignKeys = true }.ToString();
    public SqliteDatabase(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "番茄时钟", "Data", "pomodoro.db");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        using var c = Open();
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        cmd.ExecuteNonQuery();
        Migrate(c);
    }
    public SqliteConnection Open() => new(ConnectionString);
    private static void Migrate(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS SchemaVersions(Version INTEGER PRIMARY KEY, AppliedUtc TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS WorkItems(Id TEXT PRIMARY KEY, Title TEXT NOT NULL, Notes TEXT NOT NULL DEFAULT '', Status INTEGER NOT NULL, Priority INTEGER NOT NULL, CreatedUtc TEXT NOT NULL, CompletedUtc TEXT NULL, ParticipationCount INTEGER NOT NULL DEFAULT 0, IsArchived INTEGER NOT NULL DEFAULT 0);
CREATE TABLE IF NOT EXISTS FocusSessions(Id TEXT PRIMARY KEY, StartedUtc TEXT NOT NULL, EndedUtc TEXT NOT NULL, DurationSeconds INTEGER NOT NULL, CompletedUtc TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS FocusSessionWorkItems(SessionId TEXT NOT NULL REFERENCES FocusSessions(Id), WorkItemId TEXT NOT NULL REFERENCES WorkItems(Id), TitleSnapshot TEXT NOT NULL, PRIMARY KEY(SessionId, WorkItemId));
CREATE TABLE IF NOT EXISTS AppSettings(Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS IX_WorkItems_StatusPriority ON WorkItems(Status, Priority DESC, CreatedUtc);
CREATE INDEX IF NOT EXISTS IX_FocusSessions_StartedUtc ON FocusSessions(StartedUtc);
CREATE INDEX IF NOT EXISTS IX_FocusSessions_CompletedUtc ON FocusSessions(CompletedUtc);
INSERT OR IGNORE INTO SchemaVersions(Version, AppliedUtc) VALUES (1, $now);
INSERT OR IGNORE INTO AppSettings(Key, Value) VALUES ('FocusMinutes','25'),('ShortBreakMinutes','5'),('LongBreakMinutes','15'),('RoundsBeforeLongBreak','4'),('CompletedRounds','0');";
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }
}

public sealed class LocalFileLogger
{
    private readonly string _file = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "番茄时钟", "Logs", $"app-{DateTime.Now:yyyyMMdd}.log");
    public void Error(Exception ex, string context) { try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_file)!); File.AppendAllText(_file, $"[{DateTimeOffset.Now:O}] {context}: {ex}\r\n"); } catch { } }
}

public sealed class SqliteWorkItemRepository : IWorkItemRepository
{
    private readonly SqliteDatabase _db; public SqliteWorkItemRepository(SqliteDatabase db) => _db = db;
    public async Task<IReadOnlyList<WorkItem>> GetAsync(bool includeArchived = false, CancellationToken cancellationToken = default)
    {
        await using var c = _db.Open(); await c.OpenAsync(cancellationToken); await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id,Title,Notes,Status,Priority,CreatedUtc,CompletedUtc,ParticipationCount,IsArchived FROM WorkItems WHERE ($include=1 OR IsArchived=0) ORDER BY Status, Priority DESC, CreatedUtc DESC";
        cmd.Parameters.AddWithValue("$include", includeArchived ? 1 : 0); var list = new List<WorkItem>();
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken); while (await r.ReadAsync(cancellationToken)) list.Add(Read(r)); return list;
    }
    public async Task<WorkItem> SaveAsync(WorkItem item, CancellationToken cancellationToken = default)
    {
        await using var c = _db.Open(); await c.OpenAsync(cancellationToken); await using var cmd = c.CreateCommand();
        cmd.CommandText = @"INSERT INTO WorkItems(Id,Title,Notes,Status,Priority,CreatedUtc,CompletedUtc,ParticipationCount,IsArchived) VALUES($id,$title,$notes,$status,$priority,$created,$completed,$count,$archived)
ON CONFLICT(Id) DO UPDATE SET Title=$title, Notes=$notes, Status=$status, Priority=$priority, CompletedUtc=$completed, IsArchived=$archived;";
        Add(cmd, item); await cmd.ExecuteNonQueryAsync(cancellationToken); return item;
    }
    public async Task SetCompletedAsync(Guid id, bool completed, CancellationToken cancellationToken = default)
    {
        await using var c = _db.Open(); await c.OpenAsync(cancellationToken); await using var cmd = c.CreateCommand(); cmd.CommandText = "UPDATE WorkItems SET Status=$status, CompletedUtc=$completed WHERE Id=$id AND IsArchived=0"; cmd.Parameters.AddWithValue("$id", id.ToString()); cmd.Parameters.AddWithValue("$status", completed ? 1 : 0); cmd.Parameters.AddWithValue("$completed", completed ? DateTimeOffset.UtcNow.ToString("O") : (object)DBNull.Value); await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
    public async Task ArchiveAsync(Guid id, CancellationToken cancellationToken = default) { await using var c = _db.Open(); await c.OpenAsync(cancellationToken); await using var cmd = c.CreateCommand(); cmd.CommandText = "UPDATE WorkItems SET IsArchived=1 WHERE Id=$id"; cmd.Parameters.AddWithValue("$id", id.ToString()); await cmd.ExecuteNonQueryAsync(cancellationToken); }
    private static void Add(SqliteCommand cmd, WorkItem i) { cmd.Parameters.AddWithValue("$id", i.Id.ToString()); cmd.Parameters.AddWithValue("$title", i.Title); cmd.Parameters.AddWithValue("$notes", i.Notes); cmd.Parameters.AddWithValue("$status", (int)i.Status); cmd.Parameters.AddWithValue("$priority", (int)i.Priority); cmd.Parameters.AddWithValue("$created", i.CreatedUtc.ToString("O")); cmd.Parameters.AddWithValue("$completed", i.CompletedUtc?.ToString("O") ?? (object)DBNull.Value); cmd.Parameters.AddWithValue("$count", i.ParticipationCount); cmd.Parameters.AddWithValue("$archived", i.IsArchived ? 1 : 0); }
    internal static WorkItem Read(SqliteDataReader r) => new(Guid.Parse(r.GetString(0)), r.GetString(1), r.GetString(2), (WorkItemStatus)r.GetInt32(3), (WorkItemPriority)r.GetInt32(4), DateTimeOffset.Parse(r.GetString(5)), r.IsDBNull(6) ? null : DateTimeOffset.Parse(r.GetString(6)), r.GetInt32(7), r.GetInt32(8) != 0);
}

public sealed class SqliteSessionRepository : IFocusSessionRepository, IStatisticsService, IAppSettingsRepository
{
    private readonly SqliteDatabase _db; public SqliteSessionRepository(SqliteDatabase db) => _db = db;
    public async Task SaveSettlementAsync(FocusSession session, IReadOnlyCollection<Guid> workItemIds, string? newTitle, int completedRounds, bool longBreakDue, CancellationToken cancellationToken = default)
    {
        await using var c = _db.Open(); await c.OpenAsync(cancellationToken); await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(cancellationToken);
        try
        {
            await Exec(c, tx, "INSERT OR IGNORE INTO FocusSessions(Id,StartedUtc,EndedUtc,DurationSeconds,CompletedUtc) VALUES($id,$start,$end,$duration,$completed)", ("$id", session.Id.ToString()), ("$start", session.StartedUtc.ToString("O")), ("$end", session.EndedUtc.ToString("O")), ("$duration", session.DurationSeconds), ("$completed", DateTimeOffset.UtcNow.ToString("O")));
            var ids = new List<Guid>(workItemIds);
            if (!string.IsNullOrWhiteSpace(newTitle)) { var id = Guid.NewGuid(); await Exec(c, tx, "INSERT INTO WorkItems(Id,Title,Notes,Status,Priority,CreatedUtc,ParticipationCount) VALUES($id,$title,'',0,1,$created,0)", ("$id", id.ToString()), ("$title", newTitle.Trim()), ("$created", DateTimeOffset.UtcNow.ToString("O"))); ids.Add(id); }
            foreach (var id in ids.Distinct())
            {
                await using var titleCmd = c.CreateCommand(); titleCmd.Transaction = tx; titleCmd.CommandText = "SELECT Title FROM WorkItems WHERE Id=$id AND IsArchived=0"; titleCmd.Parameters.AddWithValue("$id", id.ToString()); var title = (string?)await titleCmd.ExecuteScalarAsync(cancellationToken); if (title is null) continue;
                var changed = await Exec(c, tx, "INSERT OR IGNORE INTO FocusSessionWorkItems(SessionId,WorkItemId,TitleSnapshot) VALUES($session,$item,$title)", ("$session", session.Id.ToString()), ("$item", id.ToString()), ("$title", title));
                if (changed > 0) await Exec(c, tx, "UPDATE WorkItems SET ParticipationCount=ParticipationCount+1 WHERE Id=$id", ("$id", id.ToString()));
            }
            await Exec(c, tx, "INSERT INTO AppSettings(Key,Value) VALUES('CompletedRounds',$rounds) ON CONFLICT(Key) DO UPDATE SET Value=$rounds", ("$rounds", completedRounds));
            await tx.CommitAsync(cancellationToken);
        }
        catch { await tx.RollbackAsync(cancellationToken); throw; }
    }
    public async Task<IReadOnlyList<SessionDetails>> GetSessionsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        var (start,end) = Bounds(from,to); await using var c = _db.Open(); await c.OpenAsync(cancellationToken); await using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT s.Id,s.StartedUtc,s.EndedUtc,s.DurationSeconds, group_concat(w.TitleSnapshot, '、') FROM FocusSessions s LEFT JOIN FocusSessionWorkItems w ON w.SessionId=s.Id WHERE s.StartedUtc >= $start AND s.StartedUtc < $end GROUP BY s.Id ORDER BY s.StartedUtc DESC"; cmd.Parameters.AddWithValue("$start", start); cmd.Parameters.AddWithValue("$end", end); var list = new List<SessionDetails>(); await using var r = await cmd.ExecuteReaderAsync(cancellationToken); while (await r.ReadAsync(cancellationToken)) { var s = new FocusSession(Guid.Parse(r.GetString(0)), DateTimeOffset.Parse(r.GetString(1)), DateTimeOffset.Parse(r.GetString(2)), r.GetInt32(3)); list.Add(new(s, r.IsDBNull(4) ? Array.Empty<string>() : r.GetString(4).Split('、', StringSplitOptions.RemoveEmptyEntries))); } return list;
    }
    public async Task<StatisticsSummary> GetAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        var sessions = await GetSessionsAsync(from,to,cancellationToken); var days = new List<DailySummary>(); for (var d=from; d<=to; d=d.AddDays(1)) { var ds=sessions.Where(x=>DateOnly.FromDateTime(x.Session.StartedUtc.ToLocalTime().DateTime)==d).ToList(); days.Add(new(d, ds.Count, ds.Sum(x=>x.Session.DurationSeconds)/60, await CompletedCount(d,cancellationToken))); } return new(from,to,sessions.Count,sessions.Sum(x=>x.Session.DurationSeconds)/60,days.Sum(x=>x.CompletedItems),days,sessions);
    }
    public async Task<TimerSettings> GetTimerSettingsAsync(CancellationToken cancellationToken = default) { var vals = await Settings(cancellationToken); return new(int.Parse(vals["FocusMinutes"]),int.Parse(vals["ShortBreakMinutes"]),int.Parse(vals["LongBreakMinutes"]),int.Parse(vals["RoundsBeforeLongBreak"])); }
    public async Task SaveTimerSettingsAsync(TimerSettings s, CancellationToken cancellationToken = default) { s.Validate(); foreach(var p in new[]{("FocusMinutes",s.FocusMinutes),("ShortBreakMinutes",s.ShortBreakMinutes),("LongBreakMinutes",s.LongBreakMinutes),("RoundsBeforeLongBreak",s.RoundsBeforeLongBreak)}) await Set(p.Item1,p.Item2.ToString(),cancellationToken); }
    public async Task<int> GetCompletedRoundsAsync(CancellationToken cancellationToken = default) { var v=await Get("CompletedRounds",cancellationToken); return int.TryParse(v,out var n)?n:0; }
    private async Task<int> CompletedCount(DateOnly d,CancellationToken ct) { var (s,e)=Bounds(d,d); await using var c=_db.Open(); await c.OpenAsync(ct); await using var cmd=c.CreateCommand(); cmd.CommandText="SELECT count(*) FROM WorkItems WHERE Status=1 AND CompletedUtc >= $s AND CompletedUtc < $e"; cmd.Parameters.AddWithValue("$s",s);cmd.Parameters.AddWithValue("$e",e);return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)); }
    private async Task<Dictionary<string,string>> Settings(CancellationToken ct) { await using var c=_db.Open();await c.OpenAsync(ct);await using var cmd=c.CreateCommand();cmd.CommandText="SELECT Key,Value FROM AppSettings";var d=new Dictionary<string,string>();await using var r=await cmd.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))d[r.GetString(0)]=r.GetString(1);return d; }
    private async Task<string?> Get(string key,CancellationToken ct){await using var c=_db.Open();await c.OpenAsync(ct);await using var cmd=c.CreateCommand();cmd.CommandText="SELECT Value FROM AppSettings WHERE Key=$key";cmd.Parameters.AddWithValue("$key",key);return (string?)await cmd.ExecuteScalarAsync(ct);}
    private async Task Set(string key,string value,CancellationToken ct){await using var c=_db.Open();await c.OpenAsync(ct);await using var cmd=c.CreateCommand();cmd.CommandText="INSERT INTO AppSettings(Key,Value) VALUES($key,$value) ON CONFLICT(Key) DO UPDATE SET Value=$value";cmd.Parameters.AddWithValue("$key",key);cmd.Parameters.AddWithValue("$value",value);await cmd.ExecuteNonQueryAsync(ct);}
    private static async Task<int> Exec(SqliteConnection c, SqliteTransaction tx, string sql, params (string,object)[] ps){await using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText=sql;foreach(var p in ps)cmd.Parameters.AddWithValue(p.Item1,p.Item2);return await cmd.ExecuteNonQueryAsync();}
    private static (string,string) Bounds(DateOnly from,DateOnly to){var s=from.ToDateTime(TimeOnly.MinValue,DateTimeKind.Local).ToUniversalTime();var e=to.AddDays(1).ToDateTime(TimeOnly.MinValue,DateTimeKind.Local).ToUniversalTime();return (s.ToString("O"),e.ToString("O"));}
}
