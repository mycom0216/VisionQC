// ============================================================================
// SqliteAccountRepository.cs
// 역할: 로그인 계정(user_account)과 감사 로그(audit_log)를 SQLite에 저장·조회한다.
// 파이프라인에서의 위치: 검사 파이프라인(이미지 -> 전처리 -> 추론 -> 판정 -> 이력)과는 별개로,
//   "누가 앱을 쓸 수 있는가(인증)"와 "누가 무엇을 했는가(감사)"를 담당하는 UI 앞단의 보안 계층이다.
// 용어:
//   · 역할 기반 접근 제어(Role-based access): 계정마다 역할(Operator 작업자 / Engineer 엔지니어 / Admin 관리자)을 주고,
//     역할에 따라 화면·기능을 제한한다. 권한 판단 자체는 Core의 서비스가 하고, 여기서는 역할 값을 저장만 한다.
//   · 감사 로그(audit log): 로그인·설정 변경·삭제 같은 중요한 행위를 시간순으로 남기는 기록. 사후 추적용이므로 수정·삭제 기능을 만들지 않는다.
//   · 비밀번호는 평문이 아니라 해시(되돌릴 수 없는 변환값)만 저장한다. 해시 생성·검증과 잠금 정책은 Core 쪽 서비스가 하고,
//     이 저장소는 전달받은 해시 문자열과 실패 횟수/잠금 해제 시각을 그대로 보관한다.
// ============================================================================
using System.Globalization;
using Microsoft.Data.Sqlite;
using VisionQC.Core;

namespace VisionQC.Data;

/// <summary>
/// 계정·감사 로그 저장소(검사 이력과 같은 DB 파일). 아이디는 대소문자 구분 없이 유일.
/// 감사 로그는 추가·조회만 제공한다(수정·삭제 메서드 없음). 시각은 UTC ISO-8601.
/// </summary>
public sealed class SqliteAccountRepository : IUserRepository, IAuditRepository
{
    private readonly string _connectionString;

    /// <summary>저장소를 만든다. DB 폴더/테이블이 없으면 만든다(검사 이력과 같은 DB 파일을 공유할 수 있다).</summary>
    /// <param name="dbPath">SQLite DB 파일 경로</param>
    public SqliteAccountRepository(string dbPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = true }.ToString();
        Initialize();
    }

    /// <summary>연결을 열고 외래 키 검사 ON, 잠금 대기 5초를 설정한다(SQLite는 연결마다 설정해야 한다).</summary>
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
    /// 스키마 생성(IF NOT EXISTS라 반복 실행해도 안전). SQL 문자열 안에는 주석을 못 넣으므로 설명을 여기에 둔다.
    ///  · user_account.login_id: UNIQUE + COLLATE NOCASE — 아이디는 대소문자 구분 없이 중복될 수 없다(Admin과 admin은 같은 아이디).
    ///  · password_hash: 해시 문자열만 저장(평문 저장 금지).
    ///  · role: CHECK로 세 역할만 허용 — 코드 버그가 있어도 DB에 이상한 역할이 들어가지 못한다.
    ///  · active: 1=사용, 0=비활성(계정을 지우지 않고 로그인만 막는다. 감사 로그의 과거 기록과 연결을 유지하기 위함).
    ///  · failed_count / locked_until: 로그인 연속 실패 횟수와 잠금 해제 시각(UTC). 일정 횟수 실패 시 잠가 무차별 대입(brute force)을 늦춘다.
    ///  · audit_log: 시각(at), 행위자(user_id는 NULL 가능, login_id는 당시 아이디 문자열 복사본 — 계정이 바뀌어도 기록이 남는다),
    ///    행위(action), 상세(detail). at/action 인덱스는 기간·행위별 조회용.
    /// </summary>
    private void Initialize()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS user_account (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                login_id TEXT NOT NULL UNIQUE COLLATE NOCASE,
                password_hash TEXT NOT NULL,
                role TEXT NOT NULL CHECK (role IN ('Operator','Engineer','Admin')),
                active INTEGER NOT NULL DEFAULT 1,
                failed_count INTEGER NOT NULL DEFAULT 0,
                locked_until TEXT NULL,
                created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS audit_log (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                at TEXT NOT NULL,
                user_id INTEGER NULL,
                login_id TEXT NOT NULL,
                action TEXT NOT NULL,
                detail TEXT NULL);
            CREATE INDEX IF NOT EXISTS ix_audit_at ON audit_log(at);
            CREATE INDEX IF NOT EXISTS ix_audit_action ON audit_log(action);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>UTC ISO-8601 문자열로 변환(문자열 정렬 = 시간순).</summary>
    private static string Iso(DateTime utc) => utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
    /// <summary>저장된 ISO-8601 문자열을 UTC 정보가 유지된 DateTime으로 되돌린다.</summary>
    private static DateTime ParseIso(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    // SELECT할 열 목록과 ReadAccount/ReadRecord의 열 번호(0~7)는 반드시 같은 순서여야 한다.
    private const string UserColumns = "id,login_id,role,active,created_at,password_hash,failed_count,locked_until";

    /// <summary>공개해도 되는 계정 정보(해시 제외)만 읽는다. 화면 목록 등에 쓰인다.</summary>
    private static UserAccount ReadAccount(SqliteDataReader rd) => new(
        rd.GetInt64(0), rd.GetString(1), Enum.Parse<Role>(rd.GetString(2)), rd.GetInt32(3) != 0, ParseIso(rd.GetString(4)));

    /// <summary>로그인 검증용 전체 정보(계정 + 비밀번호 해시 + 실패 횟수 + 잠금 해제 시각)를 읽는다. 인증 로직 안에서만 쓰는 것이 원칙.</summary>
    private static UserRecord ReadRecord(SqliteDataReader rd) => new(
        ReadAccount(rd), rd.GetString(5), rd.GetInt32(6), rd.IsDBNull(7) ? null : ParseIso(rd.GetString(7)));

    /// <summary>전체 계정 수. 0이면 최초 실행이므로 첫 관리자 만들기 화면을 보여 줄 때 쓴다.</summary>
    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM user_account";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    /// <summary>활성 관리자 수. 마지막 관리자를 비활성화/강등해 아무도 관리할 수 없게 되는 사고를 막기 위한 검사에 쓴다.</summary>
    public async Task<int> CountActiveAdminsAsync(CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM user_account WHERE role='Admin' AND active=1";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    /// <summary>아이디로 계정을 찾는다(대소문자 무시, 없으면 null). 로그인 시 해시 검증에 쓰는 전체 정보를 돌려준다.</summary>
    public async Task<UserRecord?> FindByLoginIdAsync(string loginId, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {UserColumns} FROM user_account WHERE login_id=$l";
        cmd.Parameters.AddWithValue("$l", loginId);
        await using var rd = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await rd.ReadAsync(ct).ConfigureAwait(false) ? ReadRecord(rd) : null;
    }

    /// <summary>내부 id로 계정을 찾는다(없으면 null).</summary>
    public async Task<UserRecord?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {UserColumns} FROM user_account WHERE id=$i";
        cmd.Parameters.AddWithValue("$i", id);
        await using var rd = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await rd.ReadAsync(ct).ConfigureAwait(false) ? ReadRecord(rd) : null;
    }

    /// <summary>
    /// 계정이 하나도 없을 때만 첫 관리자(Admin)를 만든다. 이미 계정이 있으면 null.
    /// 확인(NOT EXISTS)과 삽입을 한 SQL 문장으로 처리하므로, 두 프로세스가 동시에 시도해도 관리자가 둘 생기는 경합이 없다.
    /// </summary>
    /// <param name="passwordHash">이미 해시된 비밀번호(평문을 받지 않는다)</param>
    public async Task<UserAccount?> CreateFirstAdminAsync(string loginId, string passwordHash, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        // 계정이 하나도 없을 때만 삽입(조건 확인과 삽입이 한 문장이라 경합해도 하나만 생긴다)
        cmd.CommandText = """
            INSERT INTO user_account(login_id,password_hash,role,active,created_at)
            SELECT $l,$h,'Admin',1,$at WHERE NOT EXISTS (SELECT 1 FROM user_account);
            SELECT changes();
            """;
        cmd.Parameters.AddWithValue("$l", loginId);
        cmd.Parameters.AddWithValue("$h", passwordHash);
        cmd.Parameters.AddWithValue("$at", Iso(DateTime.UtcNow));
        // changes() = 직전 문장이 바꾼 행 수. 0이면 이미 계정이 있어 삽입이 일어나지 않은 것.
        if (Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) == 0) return null;
        return (await FindByLoginIdAsync(loginId, ct).ConfigureAwait(false))!.Account;
    }

    /// <summary>새 계정을 만든다(활성 상태). 같은 아이디(대소문자 무시)가 있으면 <see cref="DuplicateUserException"/>.</summary>
    public async Task<UserAccount> CreateAsync(string loginId, string passwordHash, Role role, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO user_account(login_id,password_hash,role,active,created_at) VALUES($l,$h,$r,1,$at)";
        cmd.Parameters.AddWithValue("$l", loginId);
        cmd.Parameters.AddWithValue("$h", passwordHash);
        cmd.Parameters.AddWithValue("$r", role.ToString());
        cmd.Parameters.AddWithValue("$at", Iso(DateTime.UtcNow));
        // 중복 확인을 먼저 SELECT로 하지 않고 UNIQUE 제약 위반(SQLite 오류 19 / 확장코드 2067)을 잡는다 — 확인과 삽입 사이의 경합이 없다.
        try { await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == 2067 || ex.SqliteErrorCode == 19 && ex.Message.Contains("UNIQUE"))
        {
            throw new DuplicateUserException(loginId);
        }
        return (await FindByLoginIdAsync(loginId, ct).ConfigureAwait(false))!.Account;
    }

    /// <summary>전체 계정 목록(id 순). 비밀번호 해시는 포함하지 않는다.</summary>
    public async Task<IReadOnlyList<UserAccount>> ListAsync(CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {UserColumns} FROM user_account ORDER BY id";
        var list = new List<UserAccount>();
        await using var rd = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await rd.ReadAsync(ct).ConfigureAwait(false)) list.Add(ReadAccount(rd));
        return list;
    }

    /// <summary>계정 사용/비활성 전환. 삭제 대신 비활성화해 감사 로그와의 연결을 보존한다.</summary>
    public async Task SetActiveAsync(long id, bool active, CancellationToken ct = default)
        => await ExecAsync("UPDATE user_account SET active=$a WHERE id=$i", ct, ("$a", active ? 1 : 0), ("$i", id));

    /// <summary>로그인 실패 횟수와 잠금 해제 시각을 갱신한다(성공 시 0/null로 초기화하는 데도 쓴다). 잠금 정책 판단은 호출하는 서비스가 한다.</summary>
    public async Task SetFailureStateAsync(long id, int failedCount, DateTime? lockedUntilUtc, CancellationToken ct = default)
        => await ExecAsync("UPDATE user_account SET failed_count=$f, locked_until=$u WHERE id=$i", ct,
            ("$f", failedCount), ("$u", lockedUntilUtc is { } u ? (object)Iso(u) : DBNull.Value), ("$i", id));

    /// <summary>비밀번호 해시를 교체한다(비밀번호 변경/초기화). 평문이 아니라 해시를 받는다.</summary>
    public async Task SetPasswordHashAsync(long id, string passwordHash, CancellationToken ct = default)
        => await ExecAsync("UPDATE user_account SET password_hash=$h WHERE id=$i", ct, ("$h", passwordHash), ("$i", id));

    /// <summary>값을 이름 붙은 매개변수로 바인딩해 단일 UPDATE/INSERT를 실행하는 공통 도우미(SQL 인젝션 방지).</summary>
    private async Task ExecAsync(string sql, CancellationToken ct, params (string Name, object Value)[] ps)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>감사 로그 한 줄을 추가한다. 이 저장소에는 감사 로그를 고치거나 지우는 메서드가 의도적으로 없다(변조 방지).</summary>
    public async Task AppendAsync(AuditEntry e, CancellationToken ct = default)
        => await ExecAsync("INSERT INTO audit_log(at,user_id,login_id,action,detail) VALUES($at,$u,$l,$a,$d)", ct,
            ("$at", Iso(e.AtUtc)), ("$u", e.UserId is { } id ? (object)id : DBNull.Value), ("$l", e.LoginId),
            ("$a", e.Action), ("$d", (object?)e.Detail ?? DBNull.Value));

    /// <summary>감사 로그를 기간·행위(action) 조건으로 최신순 조회한다(최대 1~10000건으로 제한).</summary>
    public async Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditFilter f, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        var where = new List<string>();
        if (f.FromUtc is { } from) { where.Add("at >= $from"); cmd.Parameters.AddWithValue("$from", Iso(from)); }
        if (f.ToUtc is { } to) { where.Add("at <= $to"); cmd.Parameters.AddWithValue("$to", Iso(to)); }
        if (!string.IsNullOrEmpty(f.Action)) { where.Add("action = $a"); cmd.Parameters.AddWithValue("$a", f.Action); }
        cmd.CommandText = "SELECT id,at,user_id,login_id,action,detail FROM audit_log"
            + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
            + " ORDER BY at DESC, id DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(f.Limit, 1, 10000));

        var list = new List<AuditEntry>();
        await using var rd = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await rd.ReadAsync(ct).ConfigureAwait(false))
            list.Add(new AuditEntry(rd.GetInt64(0), ParseIso(rd.GetString(1)), rd.IsDBNull(2) ? null : rd.GetInt64(2),
                rd.GetString(3), rd.GetString(4), rd.IsDBNull(5) ? null : rd.GetString(5)));
        return list;
    }
}
