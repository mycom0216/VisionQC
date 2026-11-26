// ============================================================================
// SqliteResultRepository.cs
// 역할: "검사 이력"(검사 회차·개별 검사 결과·불량 위치·검수자 정정)을 SQLite 파일에 저장하고 조회하는 저장소.
// 파이프라인에서의 위치:
//   이미지 소스 -> 전처리 -> 추론(ONNX) -> 판정(OK/NG/ERROR) -> [여기: 이력 저장소] -> UI(이력/통계 화면)
// 용어:
//   · 판정(Verdict): OK = 정상, NG = 불량 의심, ERROR = 검사 자체가 실패(이미지 손상·추론 오류 등).
//   · 점수(score): 모델이 낸 불량 가능성 값. 임계값(threshold) 이상이면 NG로 판정한다.
//   · 회차(job): 한 번의 "검사 시작~종료" 묶음. 어떤 모델/임계값으로 돌렸는지를 함께 기록한다.
// 인터페이스 IResultRepository는 VisionQC.Core에 있고, 이 클래스는 그 SQLite 구현이다(UI/Core는 SQLite를 모른다).
// ============================================================================
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
    // 연결 문자열은 한 번만 만들어 두고, 각 메서드가 필요할 때마다 새 연결을 연다(연결 풀링으로 열고 닫는 비용이 작다).
    private readonly string _connectionString;

    /// <summary>
    /// 저장소를 만든다. DB 파일의 폴더가 없으면 만들고, 테이블이 없으면 생성한다(첫 실행 / 업그레이드 모두 이 한 번의 호출로 준비된다).
    /// </summary>
    /// <param name="dbPath">SQLite DB 파일 경로(없으면 새로 만들어진다)</param>
    public SqliteResultRepository(string dbPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = true }.ToString();
        Initialize();
    }

    /// <summary>
    /// 연결을 열고 세션 설정을 적용한다.
    /// foreign_keys = ON: SQLite는 기본적으로 외래 키 제약을 검사하지 않으므로 연결마다 켜야 한다(없는 결과에 불량/정정을 붙이는 것을 막는다).
    /// busy_timeout = 5000: 다른 연결이 쓰는 중이면 곧바로 "database is locked" 오류를 내지 않고 최대 5초 기다린다.
    /// </summary>
    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        cmd.ExecuteNonQuery();
        return c;
    }

    /// <summary>
    /// 스키마 생성. 모든 문장이 IF NOT EXISTS라서 여러 번 실행해도 안전하다(멱등).
    /// 테이블 관계: inspection_job 1 - N inspection_result 1 - N defect, inspection_result 1 - N result_review.
    /// (SQL 문자열 안에는 주석을 넣을 수 없어 설명을 여기에 모아 둔다.)
    ///  · journal_mode = WAL: 쓰기 로그 방식. 읽기와 쓰기가 서로 막지 않아 검사 중에도 이력 화면을 볼 수 있고, 비정상 종료에도 강하다.
    ///  · inspection_job: 회차. model_name/model_version/threshold는 "그때 어떤 모델·기준으로 검사했는지" 재현하기 위한 기록,
    ///    status는 Running/Completed 등(JobStatus), ended_at은 진행 중이면 NULL.
    ///  · inspection_result: 이미지 1장의 결과. image_path는 파일 경로만 저장(이미지 자체는 DB에 넣지 않는다), verdict는 OK/NG/ERROR 문자열,
    ///    elapsed_ms는 처리 시간, error_message는 ERROR일 때 원인. job_id는 외래 키(회차가 없으면 결과를 넣을 수 없다).
    ///  · defect: 한 결과에서 찾은 불량 위치 목록. 박스 좌표 x,y,w,h(픽셀)와 불량 종류·신뢰도. 결과를 지우면 ON DELETE CASCADE로 같이 지워진다.
    ///  · 인덱스: 이력 화면의 기간/판정/회차 조회와 불량 조인을 빠르게 하기 위한 것(없으면 행이 많을 때 전체를 훑는다).
    ///  · result_review: 사람(검수자)이 모델 판정을 OK/NG로 정정한 기록. CHECK 제약으로 ERROR 같은 값은 DB 수준에서도 막는다.
    ///    원본 판정은 바꾸지 않고 정정을 별도 행으로 쌓아 "누가 언제 왜 바꿨는지"가 남는다.
    /// </summary>
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

    /// <summary>
    /// 이전 버전 DB에는 heatmap 열이 없다 — 열 때 한 번 추가한다(기존 데이터는 NULL = 히트맵 없음).
    /// 간단한 "스키마 마이그레이션": PRAGMA table_info로 현재 열 목록을 읽어 heatmap이 이미 있으면 아무것도 하지 않고,
    /// 없을 때만 ALTER TABLE로 추가한다. 그래서 새 DB와 옛 DB 모두 같은 코드 경로로 최신 구조가 된다.
    /// (heatmap = 불량 의심 위치를 색으로 보여 주는 점수 지도를 직렬화한 BLOB)
    /// </summary>
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

    /// <summary>시각을 UTC ISO-8601("o" 형식) 문자열로 바꾼다. 같은 형식이면 문자열 비교가 곧 시간 비교라 범위 조건/ORDER BY가 그대로 동작한다.</summary>
    private static string Iso(DateTime utc) => utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    /// <summary>
    /// 새 검사 회차를 시작으로 기록하고 그 id를 돌려준다(상태 Running, 종료시각 NULL).
    /// 이후 저장되는 모든 결과는 이 id를 job_id로 가진다.
    /// 값은 모두 매개변수($n 등)로 바인딩한다 — 문자열을 SQL에 이어 붙이면 SQL 인젝션 위험이 있고 따옴표가 든 값에서 깨진다.
    /// </summary>
    /// <param name="model">사용한 모델 정보(이름·버전)</param>
    /// <param name="sourceName">이미지 소스 이름(예: folder:경로)</param>
    /// <param name="threshold">이 회차의 NG 판정 임계값</param>
    public async Task<long> BeginJobAsync(ModelInfo model, string sourceName, float threshold, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        // INSERT 후 같은 연결에서 last_insert_rowid()로 새 행 id를 즉시 얻는다(연결이 같아야 값이 맞다).
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

    /// <summary>회차를 종료 상태(완료/취소/실패 등)로 바꾸고 종료 시각을 기록한다.</summary>
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

    /// <summary>
    /// 이미지 1장의 검사 결과와 그 불량 목록을 저장한다.
    /// 결과 행과 불량 행들을 한 트랜잭션으로 묶는다 — 중간에 실패하면 전부 롤백되어 "결과는 있는데 불량이 절반만 있는" 상태가 생기지 않는다.
    /// </summary>
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

        // 불량(검출 박스)마다 한 행씩 저장. 위에서 얻은 결과 id(id)를 외래 키로 연결한다.
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
        await tx.CommitAsync(ct).ConfigureAwait(false);   // 여기까지 와야 비로소 DB에 반영된다(예외 시 using이 트랜잭션을 롤백)
    }

    /// <summary>
    /// 필터(기간·판정·최대 개수)에 맞는 검사 결과를 최신순으로 조회한다. 불량 목록도 함께 채워 돌려준다.
    /// WHERE 조건은 필요한 것만 문자열 조각으로 모으되, 값은 항상 매개변수로 바인딩한다(조각 자체는 코드가 정한 고정 문자열이라 안전).
    /// </summary>
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
        // 한 번에 가져올 수 있는 행 수를 1~10000으로 제한(과도한 조회로 화면/메모리가 멈추는 것을 방지)
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

        // 불량 목록은 한 번에 조회(N+1 방지): 결과마다 쿼리하면 N번 왕복하므로 IN (...) 한 번으로 모아 읽고 메모리에서 결과별로 나눈다.
        // IN 목록의 이름($i0,$i1..)만 문자열로 만들고 id 값은 전부 바인딩한다.
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

    /// <summary>이력 정리 결과: 삭제된 검사 결과 수와 함께 정리된(결과가 남지 않은) 회차 수.</summary>
    public sealed record PurgeResult(int Results, int Jobs);

    /// <summary>cutoff보다 오래된 검사 결과가 몇 건인지 센다(삭제 전에 사용자에게 "N건이 지워집니다"를 보여 주기 위한 미리보기).</summary>
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
        // 결과 삭제와 빈 회차 삭제를 한 트랜잭션으로: 중간 실패 시 둘 다 취소되어 일관성이 유지된다.
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        int results, jobs;
        await using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            // 결과를 지우면 defect/result_review는 외래 키 CASCADE로 자동 삭제된다.
            cmd.CommandText = "DELETE FROM inspection_result WHERE inspected_at < $c";
            cmd.Parameters.AddWithValue("$c", Iso(cutoffUtc));
            results = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            // 결과가 하나도 남지 않은 회차만 삭제. 진행 중(Running) 회차는 아직 결과가 없을 수 있으므로 제외한다.
            cmd.CommandText = "DELETE FROM inspection_job WHERE status <> 'Running' AND id NOT IN (SELECT DISTINCT job_id FROM inspection_result)";
            jobs = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new PurgeResult(results, jobs);
    }

    /// <summary>
    /// 검수자의 판정 정정을 추가한다. 정정은 OK 또는 NG만 허용한다(ERROR는 "사람이 정한 판정"이 될 수 없음).
    /// 같은 결과에 여러 번 정정할 수 있고, 가장 나중 것이 현재 유효한 정정이다(이전 기록은 이력으로 남는다).
    /// </summary>
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

    /// <summary>result_review 조회 결과의 현재 행(열 순서 고정)을 Review 객체로 바꾼다.</summary>
    private static Review ReadReview(SqliteDataReader rd) => new(
        rd.GetInt64(0), rd.GetInt64(1), rd.GetString(2), Enum.Parse<Verdict>(rd.GetString(3)),
        rd.IsDBNull(4) ? null : rd.GetString(4),
        DateTime.Parse(rd.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    /// <summary>한 검사 결과에 달린 모든 정정을 오래된 순으로 돌려준다.</summary>
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

    /// <summary>검사 결과 목록(QueryAsync와 같은 필터)에 각 결과의 최신 정정과 정정 횟수를 붙여 돌려준다.</summary>
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

    /// <summary>
    /// 통계 화면용 원자료를 조회한다: ① 기간 내 (시각, 판정, 처리시간) 행 ② 불량 종류별 건수.
    /// 집계(수율·시간대별 추이 등)는 호출하는 쪽이 StatsData로 계산한다 — 여기서는 필요한 최소 열만 읽어 온다.
    /// </summary>
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

        // 불량 종류별 개수: defect를 결과와 조인해 같은 기간 조건을 적용하고 종류로 GROUP BY
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
