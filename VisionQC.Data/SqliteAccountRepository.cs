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

    public SqliteAccountRepository(string dbPath)
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

    private static string Iso(DateTime utc) => utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
    private static DateTime ParseIso(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private const string UserColumns = "id,login_id,role,active,created_at,password_hash,failed_count,locked_until";

    private static UserAccount ReadAccount(SqliteDataReader rd) => new(
        rd.GetInt64(0), rd.GetString(1), Enum.Parse<Role>(rd.GetString(2)), rd.GetInt32(3) != 0, ParseIso(rd.GetString(4)));

    private static UserRecord ReadRecord(SqliteDataReader rd) => new(
        ReadAccount(rd), rd.GetString(5), rd.GetInt32(6), rd.IsDBNull(7) ? null : ParseIso(rd.GetString(7)));

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM user_account";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    public async Task<int> CountActiveAdminsAsync(CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM user_account WHERE role='Admin' AND active=1";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    public async Task<UserRecord?> FindByLoginIdAsync(string loginId, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {UserColumns} FROM user_account WHERE login_id=$l";
        cmd.Parameters.AddWithValue("$l", loginId);
        await using var rd = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await rd.ReadAsync(ct).ConfigureAwait(false) ? ReadRecord(rd) : null;
    }

    public async Task<UserRecord?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {UserColumns} FROM user_account WHERE id=$i";
        cmd.Parameters.AddWithValue("$i", id);
        await using var rd = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await rd.ReadAsync(ct).ConfigureAwait(false) ? ReadRecord(rd) : null;
    }

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
        if (Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) == 0) return null;
        return (await FindByLoginIdAsync(loginId, ct).ConfigureAwait(false))!.Account;
    }

    public async Task<UserAccount> CreateAsync(string loginId, string passwordHash, Role role, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO user_account(login_id,password_hash,role,active,created_at) VALUES($l,$h,$r,1,$at)";
        cmd.Parameters.AddWithValue("$l", loginId);
        cmd.Parameters.AddWithValue("$h", passwordHash);
        cmd.Parameters.AddWithValue("$r", role.ToString());
        cmd.Parameters.AddWithValue("$at", Iso(DateTime.UtcNow));
        try { await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == 2067 || ex.SqliteErrorCode == 19 && ex.Message.Contains("UNIQUE"))
        {
            throw new DuplicateUserException(loginId);
        }
        return (await FindByLoginIdAsync(loginId, ct).ConfigureAwait(false))!.Account;
    }

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

    public async Task SetActiveAsync(long id, bool active, CancellationToken ct = default)
        => await ExecAsync("UPDATE user_account SET active=$a WHERE id=$i", ct, ("$a", active ? 1 : 0), ("$i", id));

    public async Task SetFailureStateAsync(long id, int failedCount, DateTime? lockedUntilUtc, CancellationToken ct = default)
        => await ExecAsync("UPDATE user_account SET failed_count=$f, locked_until=$u WHERE id=$i", ct,
            ("$f", failedCount), ("$u", lockedUntilUtc is { } u ? (object)Iso(u) : DBNull.Value), ("$i", id));

    public async Task SetPasswordHashAsync(long id, string passwordHash, CancellationToken ct = default)
        => await ExecAsync("UPDATE user_account SET password_hash=$h WHERE id=$i", ct, ("$h", passwordHash), ("$i", id));

    private async Task ExecAsync(string sql, CancellationToken ct, params (string Name, object Value)[] ps)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task AppendAsync(AuditEntry e, CancellationToken ct = default)
        => await ExecAsync("INSERT INTO audit_log(at,user_id,login_id,action,detail) VALUES($at,$u,$l,$a,$d)", ct,
            ("$at", Iso(e.AtUtc)), ("$u", e.UserId is { } id ? (object)id : DBNull.Value), ("$l", e.LoginId),
            ("$a", e.Action), ("$d", (object?)e.Detail ?? DBNull.Value));

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
