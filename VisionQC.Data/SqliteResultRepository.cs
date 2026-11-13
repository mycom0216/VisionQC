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
            CREATE TABLE IF NOT EXISTS result_review (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                result_id INTEGER NOT NULL REFERENCES inspection_result(id) ON DELETE CASCADE,
                reviewer TEXT NOT NULL,
                corrected_verdict TEXT NOT NULL CHECK (corrected_verdict IN ('OK','NG')),
                note TEXT NULL,
                reviewed_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_review_result ON result_review(result_id);
            """;
        cmd.ExecuteNonQuery();
        EnsureHeatmapColumn(c);
    }

    /// <summary>이전 버전 DB에는 heatmap 열이 없다 — 열 때 한 번 추가한다(기존 데이터는 NULL = 히트맵 없음).</summary>
    private static void EnsureHeatmapColumn(SqliteConnection c)
    {
        bool has = false;
        using (var info = c.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(inspection_result)";
            using var rd = info.ExecuteReader();
            while (rd.Read()) if (string.Equals(rd.GetString(1), "heatmap", StringComparison.OrdinalIgnoreCase)) has = true;
        }
        if (has) return;
        using var alter = c.CreateCommand();
        alter.CommandText = "ALTER TABLE inspection_result ADD COLUMN heatmap BLOB NULL";
        alter.ExecuteNonQuery();
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
                INSERT INTO inspection_result(job_id,image_path,verdict,score,elapsed_ms,error_message,inspected_at,heatmap)
                VALUES($j,$p,$v,$s,$e,$m,$at,$hm); SELECT last_insert_rowid();
                """;
            // 히트맵은 NG 결과만 저장(OK에는 의미가 없고 DB만 커진다). 직렬화에 실패해도 결과 자체는 잃지 않는다.
            object hm = DBNull.Value;
            if (r.Verdict == Verdict.NG && r.Heatmap is { } map)
            {
                try { hm = HeatmapMath.Serialize(map); } catch (ArgumentException) { }
            }
            cmd.Parameters.AddWithValue("$hm", hm);
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
        cmd.CommandText = "SELECT id,job_id,image_path,verdict,score,elapsed_ms,error_message,inspected_at,heatmap FROM inspection_result"
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
                        DateTimeStyles.RoundtripKind),
                    Heatmap = rd.IsDBNull(8) ? null : HeatmapMath.TryDeserialize((byte[])rd[8])
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

    /// <summary>이력 정리의 안전 장치: 이보다 최근 데이터는 지울 수 없다.</summary>
    public const int MinRetentionDays = 30;

    public sealed record PurgeResult(int Results, int Jobs);

    public async Task<int> CountOlderThanAsync(DateTime cutoffUtc, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM inspection_result WHERE inspected_at < $c";
        cmd.Parameters.AddWithValue("$c", Iso(cutoffUtc));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    /// <summary>
    /// cutoff보다 오래된 검사 결과를 삭제한다(불량 목록·정정은 외래 키 CASCADE로 함께 삭제). 결과가 하나도 안 남은 회차도 지운다(진행 중 회차 제외).
    /// 감사 로그·계정은 건드리지 않는다. <b>되돌릴 수 없으므로</b> 호출하는 쪽이 먼저 백업해야 하며,
    /// 실수 방지로 최근 <see cref="MinRetentionDays"/>일 이내를 가리키는 cutoff는 거부한다.
    /// </summary>
    public async Task<PurgeResult> PurgeOlderThanAsync(DateTime cutoffUtc, DateTime? nowUtc = null, CancellationToken ct = default)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        if (cutoffUtc.ToUniversalTime() > now.AddDays(-MinRetentionDays))
            throw new ArgumentOutOfRangeException(nameof(cutoffUtc), $"최근 {MinRetentionDays}일 이내의 이력은 삭제할 수 없습니다.");

        await using var c = Open();
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        int results, jobs;
        await using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM inspection_result WHERE inspected_at < $c";
            cmd.Parameters.AddWithValue("$c", Iso(cutoffUtc));
            results = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM inspection_job WHERE status <> 'Running' AND id NOT IN (SELECT DISTINCT job_id FROM inspection_result)";
            jobs = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new PurgeResult(results, jobs);
    }

    public async Task AddReviewAsync(Review r, CancellationToken ct = default)
    {
        if (r.Corrected == Verdict.ERROR)
            throw new ArgumentException("정정은 OK 또는 NG만 가능합니다.", nameof(r));
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO result_review(result_id,reviewer,corrected_verdict,note,reviewed_at) VALUES($r,$w,$v,$n,$at)";
        cmd.Parameters.AddWithValue("$r", r.ResultId);
        cmd.Parameters.AddWithValue("$w", r.Reviewer);
        cmd.Parameters.AddWithValue("$v", r.Corrected.ToString());
        cmd.Parameters.AddWithValue("$n", (object?)r.Note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", Iso(r.ReviewedAtUtc));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);   // 없는 result_id면 FK 위반으로 SqliteException
    }

    private static Review ReadReview(SqliteDataReader rd) => new(
        rd.GetInt64(0), rd.GetInt64(1), rd.GetString(2), Enum.Parse<Verdict>(rd.GetString(3)),
        rd.IsDBNull(4) ? null : rd.GetString(4),
        DateTime.Parse(rd.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    public async Task<IReadOnlyList<Review>> GetReviewsAsync(long resultId, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,result_id,reviewer,corrected_verdict,note,reviewed_at FROM result_review WHERE result_id=$r ORDER BY id";
        cmd.Parameters.AddWithValue("$r", resultId);
        var list = new List<Review>();
        await using var rd = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await rd.ReadAsync(ct).ConfigureAwait(false)) list.Add(ReadReview(rd));
        return list;
    }

    public async Task<IReadOnlyList<ReviewedResult>> QueryReviewedAsync(HistoryFilter f, CancellationToken ct = default)
    {
        var results = await QueryAsync(f, ct).ConfigureAwait(false);
        if (results.Count == 0) return Array.Empty<ReviewedResult>();

        // 정정은 한 번에 조회(N+1 방지). id 순으로 읽어 마지막이 가장 나중 정정.
        var latest = new Dictionary<long, Review>();
        var count = new Dictionary<long, int>();
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        var names = new List<string>();
        for (int i = 0; i < results.Count; i++) { names.Add($"$i{i}"); cmd.Parameters.AddWithValue($"$i{i}", results[i].Id); }
        cmd.CommandText = "SELECT id,result_id,reviewer,corrected_verdict,note,reviewed_at FROM result_review "
            + $"WHERE result_id IN ({string.Join(",", names)}) ORDER BY id";
        await using (var rd = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while (await rd.ReadAsync(ct).ConfigureAwait(false))
            {
                var rev = ReadReview(rd);
                latest[rev.ResultId] = rev;
                count[rev.ResultId] = count.GetValueOrDefault(rev.ResultId) + 1;
            }
        return results.Select(r => new ReviewedResult(r, latest.GetValueOrDefault(r.Id), count.GetValueOrDefault(r.Id))).ToList();
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
