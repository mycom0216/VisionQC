using System.Globalization;
using Microsoft.Data.Sqlite;
using VisionQC.Core;

namespace VisionQC.Data;

/// <summary>
/// SQLite 이력 저장소. 시각은 UTC ISO-8601 문자열로 저장한다(문자열 정렬 = 시간순).
/// 이미지 파일은 DB에 넣지 않고 경로만 저장한다. 연결은 호출마다 열고 닫는다(풀링).
/// </summary>
public sealed class SqliteResultRepository : IResultRepository
{
    private readonly string _connectionString;

    public SqliteResultRepository(string dbPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = true }.ToString();
        Initialize();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        cmd.ExecuteNonQuery();
        return c;
    }

    private void Initialize()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS inspection_job (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                model_name TEXT NOT NULL, model_version TEXT NOT NULL,
                source_name TEXT NOT NULL, status TEXT NOT NULL, threshold REAL NOT NULL,
                started_at TEXT NOT NULL, ended_at TEXT NULL);
            CREATE TABLE IF NOT EXISTS inspection_result (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                job_id INTEGER NOT NULL REFERENCES inspection_job(id),
                image_path TEXT NOT NULL, verdict TEXT NOT NULL, score REAL NOT NULL,
                elapsed_ms INTEGER NOT NULL, error_message TEXT NULL, inspected_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS defect (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                result_id INTEGER NOT NULL REFERENCES inspection_result(id) ON DELETE CASCADE,
                defect_type TEXT NOT NULL, confidence REAL NOT NULL,
                x INTEGER NOT NULL, y INTEGER NOT NULL, w INTEGER NOT NULL, h INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_result_time ON inspection_result(inspected_at);
            CREATE INDEX IF NOT EXISTS ix_result_verdict ON inspection_result(verdict);
            CREATE INDEX IF NOT EXISTS ix_result_job ON inspection_result(job_id);
            CREATE INDEX IF NOT EXISTS ix_defect_result ON defect(result_id);
            """;
        cmd.ExecuteNonQuery();
    }

    private static string Iso(DateTime utc) => utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    public async Task<long> BeginJobAsync(ModelInfo model, string sourceName, float threshold, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO inspection_job(model_name, model_version, source_name, status, threshold, started_at)
            VALUES($n,$v,$s,$st,$t,$at); SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$n", model.Name);
        cmd.Parameters.AddWithValue("$v", model.Version);
        cmd.Parameters.AddWithValue("$s", sourceName);
        cmd.Parameters.AddWithValue("$st", JobStatus.Running.ToString());
        cmd.Parameters.AddWithValue("$t", threshold);
        cmd.Parameters.AddWithValue("$at", Iso(DateTime.UtcNow));
        return (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    public async Task EndJobAsync(long jobId, JobStatus status, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE inspection_job SET status=$st, ended_at=$at WHERE id=$id";
        cmd.Parameters.AddWithValue("$st", status.ToString());
        cmd.Parameters.AddWithValue("$at", Iso(DateTime.UtcNow));
        cmd.Parameters.AddWithValue("$id", jobId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task SaveAsync(InspectionResult r, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);

        long id;
        await using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO inspection_result(job_id,image_path,verdict,score,elapsed_ms,error_message,inspected_at)
                VALUES($j,$p,$v,$s,$e,$m,$at); SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$j", r.JobId);
            cmd.Parameters.AddWithValue("$p", r.ImagePath);
            cmd.Parameters.AddWithValue("$v", r.Verdict.ToString());
            // NaN은 SQLite에서 NULL이 되어 NOT NULL 제약에 걸리므로 0으로 저장(판정은 ERROR로 이미 기록됨)
            cmd.Parameters.AddWithValue("$s", float.IsFinite(r.Score) ? r.Score : 0f);
            cmd.Parameters.AddWithValue("$e", r.ElapsedMs);
            cmd.Parameters.AddWithValue("$m", (object?)r.ErrorMessage ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$at", Iso(r.InspectedAtUtc));
            id = (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        }

        foreach (var d in r.Defects)
        {
            await using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO defect(result_id,defect_type,confidence,x,y,w,h) VALUES($r,$t,$c,$x,$y,$w,$h)";
            cmd.Parameters.AddWithValue("$r", id);
            cmd.Parameters.AddWithValue("$t", d.Type);
            cmd.Parameters.AddWithValue("$c", d.Confidence);
            cmd.Parameters.AddWithValue("$x", d.X);
            cmd.Parameters.AddWithValue("$y", d.Y);
            cmd.Parameters.AddWithValue("$w", d.W);
            cmd.Parameters.AddWithValue("$h", d.H);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<InspectionResult>> QueryAsync(HistoryFilter f, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        var where = new List<string>();
        if (f.FromUtc is { } from) { where.Add("inspected_at >= $from"); cmd.Parameters.AddWithValue("$from", Iso(from)); }
        if (f.ToUtc is { } to) { where.Add("inspected_at <= $to"); cmd.Parameters.AddWithValue("$to", Iso(to)); }
        if (f.Verdict is { } v) { where.Add("verdict = $v"); cmd.Parameters.AddWithValue("$v", v.ToString()); }
        cmd.CommandText = "SELECT id,job_id,image_path,verdict,score,elapsed_ms,error_message,inspected_at FROM inspection_result"
            + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
            + " ORDER BY inspected_at DESC, id DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(f.Limit, 1, 10000));

        var rows = new List<InspectionResult>();
        await using (var rd = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await rd.ReadAsync(ct).ConfigureAwait(false))
            {
                rows.Add(new InspectionResult
                {
                    Id = rd.GetInt64(0), JobId = rd.GetInt64(1), ImagePath = rd.GetString(2),
                    Verdict = Enum.Parse<Verdict>(rd.GetString(3)), Score = (float)rd.GetDouble(4),
                    ElapsedMs = rd.GetInt32(5), ErrorMessage = rd.IsDBNull(6) ? null : rd.GetString(6),
                    InspectedAtUtc = DateTime.Parse(rd.GetString(7), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind)
                });
            }
        }
        if (rows.Count == 0) return rows;

        // 불량 목록은 한 번에 조회(N+1 방지)
        var byId = rows.ToDictionary(r => r.Id);
        var defects = new Dictionary<long, List<Detection>>();
        await using (var dcmd = c.CreateCommand())
        {
            var names = new List<string>();
            int i = 0;
            foreach (var id in byId.Keys) { var n = $"$i{i++}"; names.Add(n); dcmd.Parameters.AddWithValue(n, id); }
            dcmd.CommandText = $"SELECT result_id,defect_type,confidence,x,y,w,h FROM defect WHERE result_id IN ({string.Join(",", names)})";
            await using var rd = await dcmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await rd.ReadAsync(ct).ConfigureAwait(false))
            {
                var rid = rd.GetInt64(0);
                if (!defects.TryGetValue(rid, out var list)) defects[rid] = list = new List<Detection>();
                list.Add(new Detection(rd.GetString(1), (float)rd.GetDouble(2), rd.GetInt32(3), rd.GetInt32(4), rd.GetInt32(5), rd.GetInt32(6)));
            }
        }
        return rows.Select(r => defects.TryGetValue(r.Id, out var d) ? r with { Defects = d } : r).ToList();
    }

    public async Task<StatsData> QueryStatsAsync(StatsFilter f, CancellationToken ct = default)
    {
        await using var c = Open();

        // 기간 조건은 두 쿼리에 같은 파라미터 이름으로 쓴다
        var where = new List<string>();
        void AddParams(SqliteCommand cmd)
        {
            if (f.FromUtc is { } from) cmd.Parameters.AddWithValue("$from", Iso(from));
            if (f.ToUtc is { } to) cmd.Parameters.AddWithValue("$to", Iso(to));
        }
        if (f.FromUtc is not null) where.Add("r.inspected_at >= $from");
        if (f.ToUtc is not null) where.Add("r.inspected_at <= $to");
        var cond = where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "";

        var rows = new List<StatRow>();
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT r.inspected_at, r.verdict, r.elapsed_ms FROM inspection_result r" + cond;
            AddParams(cmd);
            await using var rd = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await rd.ReadAsync(ct).ConfigureAwait(false))
                rows.Add(new StatRow(
                    DateTime.Parse(rd.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    Enum.Parse<Verdict>(rd.GetString(1)), rd.GetInt32(2)));
        }

        var defects = new List<DefectCount>();
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT d.defect_type, COUNT(*) FROM defect d JOIN inspection_result r ON r.id = d.result_id"
                + cond + " GROUP BY d.defect_type";
            AddParams(cmd);
            await using var rd = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await rd.ReadAsync(ct).ConfigureAwait(false))
                defects.Add(new DefectCount(rd.GetString(0), rd.GetInt32(1)));
        }
        return new StatsData(rows, defects);
    }
}
