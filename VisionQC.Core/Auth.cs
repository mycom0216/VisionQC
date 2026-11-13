using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace VisionQC.Core;

public enum Role { Operator = 0, Engineer = 1, Admin = 2 }

public enum Permission
{
    RunInspection, ViewHistory, ViewStats,   // 모든 역할
    ReviewResults, ChangeSettings,           // 엔지니어 이상
    ManageUsers, ViewAudit                   // 관리자
}

public static class Permissions
{
    public static bool Has(Role role, Permission p) => p switch
    {
        Permission.RunInspection or Permission.ViewHistory or Permission.ViewStats => true,
        Permission.ReviewResults or Permission.ChangeSettings => role >= Role.Engineer,
        Permission.ManageUsers or Permission.ViewAudit => role == Role.Admin,
        _ => false
    };
}

public sealed record UserAccount(long Id, string LoginId, Role Role, bool Active, DateTime CreatedAtUtc);

/// <summary>저장소에서 읽은 계정 + 인증 상태(해시·실패 횟수·잠금). UI에는 UserAccount만 넘긴다.</summary>
public sealed record UserRecord(UserAccount Account, string PasswordHash, int FailedCount, DateTime? LockedUntilUtc);

public sealed class DuplicateUserException : Exception
{
    public DuplicateUserException(string loginId) : base($"이미 사용 중인 아이디입니다: {loginId}") { }
}

public sealed record AuditEntry(long Id, DateTime AtUtc, long? UserId, string LoginId, string Action, string? Detail);

public sealed record AuditFilter(DateTime? FromUtc = null, DateTime? ToUtc = null, string? Action = null, int Limit = 500);

public static class AuditActions
{
    public const string LoginSuccess = "login.success";
    public const string LoginFail = "login.fail";
    public const string LoginLocked = "login.locked";
    public const string Logout = "logout";
    public const string InspectionStart = "inspection.start";
    public const string InspectionEnd = "inspection.end";
    public const string ReviewCorrect = "review.correct";
    public const string SettingsChange = "settings.change";
    public const string UserCreate = "user.create";
    public const string UserActivate = "user.activate";
    public const string UserDeactivate = "user.deactivate";
    public const string PasswordChange = "password.change";
    public const string PasswordReset = "password.reset";
    public const string SessionTimeout = "session.timeout";
    public const string DbBackup = "db.backup";
    public const string DbRestore = "db.restore";
    public const string HistoryPurge = "history.purge";

    public static readonly string[] All =
    {
        LoginSuccess, LoginFail, LoginLocked, Logout, SessionTimeout, InspectionStart, InspectionEnd, ReviewCorrect,
        SettingsChange, UserCreate, UserActivate, UserDeactivate, PasswordChange, PasswordReset, DbBackup, DbRestore, HistoryPurge
    };
}

public interface IUserRepository
{
    Task<int> CountAsync(CancellationToken ct = default);
    Task<int> CountActiveAdminsAsync(CancellationToken ct = default);
    Task<UserRecord?> FindByLoginIdAsync(string loginId, CancellationToken ct = default);
    Task<UserRecord?> GetAsync(long id, CancellationToken ct = default);
    /// <summary>계정이 하나도 없을 때만 관리자를 만든다(원자적). 이미 있으면 null.</summary>
    Task<UserAccount?> CreateFirstAdminAsync(string loginId, string passwordHash, CancellationToken ct = default);
    /// <summary>중복 아이디면 DuplicateUserException.</summary>
    Task<UserAccount> CreateAsync(string loginId, string passwordHash, Role role, CancellationToken ct = default);
    Task<IReadOnlyList<UserAccount>> ListAsync(CancellationToken ct = default);
    Task SetActiveAsync(long id, bool active, CancellationToken ct = default);
    Task SetFailureStateAsync(long id, int failedCount, DateTime? lockedUntilUtc, CancellationToken ct = default);
    Task SetPasswordHashAsync(long id, string passwordHash, CancellationToken ct = default);
}

/// <summary>감사 로그 저장소. 추가와 조회만 있고 수정·삭제 API는 없다.</summary>
public interface IAuditRepository
{
    Task AppendAsync(AuditEntry entry, CancellationToken ct = default);
    Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditFilter filter, CancellationToken ct = default);
}

/// <summary>
/// PBKDF2-HMAC-SHA256. 저장 형식: "PBKDF2-SHA256$반복횟수$salt(base64)$hash(base64)".
/// 비교는 고정 시간(FixedTimeEquals). 형식이 깨진 값은 예외 없이 불일치로 처리한다.
/// </summary>
public static class PasswordHasher
{
    public const int Iterations = 600_000;   // OWASP 권장(PBKDF2-HMAC-SHA256)
    private const int SaltBytes = 16, HashBytes = 32;
    private const string Prefix = "PBKDF2-SHA256";

    public static string Hash(string password) => Hash(password, Iterations);

    internal static string Hash(string password, int iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Prefix}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        try
        {
            var p = stored.Split('$');
            if (p.Length != 4 || p[0] != Prefix || !int.TryParse(p[1], out var iter) || iter < 1) return false;
            var salt = Convert.FromBase64String(p[2]);
            var expected = Convert.FromBase64String(p[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iter, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }

    private static readonly Lazy<string> Dummy = new(() => Hash("dummy-password-for-timing"));

    /// <summary>없는 아이디로 로그인해도 응답 시간이 같도록 해시 계산을 한 번 수행한다(아이디 존재 여부 추측 방지).</summary>
    public static void BurnTime(string password) => Verify(password, Dummy.Value);
}

public static class PasswordPolicy
{
    public const int MinLength = 8, MaxLength = 128;
    private static readonly Regex LoginIdRule = new(@"^[A-Za-z0-9_.\-]{3,32}$", RegexOptions.Compiled);

    /// <summary>문제가 있으면 사유, 없으면 null.</summary>
    public static string? ValidateLoginId(string? loginId)
        => loginId is not null && LoginIdRule.IsMatch(loginId.Trim())
            ? null : "아이디는 영문·숫자·_ . - 로 3~32자여야 합니다.";

    public static string? ValidatePassword(string? loginId, string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength) return $"비밀번호는 {MinLength}자 이상이어야 합니다.";
        if (password.Length > MaxLength) return $"비밀번호는 {MaxLength}자 이하여야 합니다.";
        if (!string.IsNullOrWhiteSpace(loginId) && password.Contains(loginId.Trim(), StringComparison.OrdinalIgnoreCase))
            return "비밀번호에 아이디를 포함할 수 없습니다.";
        return null;
    }
}

/// <summary>감사 기록 도우미. 기록 실패가 업무(검사·정정)를 막지 않도록 예외를 삼키고 성공 여부만 돌려준다.</summary>
public sealed class AuditLogger
{
    private readonly IAuditRepository _repo;
    private readonly Func<DateTime> _now;
    public AuditLogger(IAuditRepository repo, Func<DateTime>? now = null) { _repo = repo; _now = now ?? (() => DateTime.UtcNow); }

    public async Task<bool> WriteAsync(UserAccount? user, string loginIdFallback, string action, string? detail = null)
    {
        try
        {
            await _repo.AppendAsync(new AuditEntry(0, _now(), user?.Id, user?.LoginId ?? loginIdFallback, action, detail)).ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }
}

public sealed record LoginResult(bool Success, UserAccount? User, string Message);

public sealed class AuthService
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(10);
    public const string GenericFailure = "아이디 또는 비밀번호가 올바르지 않습니다.";

    private readonly IUserRepository _users;
    private readonly AuditLogger _audit;
    private readonly Func<DateTime> _now;

    public AuthService(IUserRepository users, AuditLogger audit, Func<DateTime>? now = null)
    {
        _users = users; _audit = audit; _now = now ?? (() => DateTime.UtcNow);
    }

    public async Task<bool> HasAnyUserAsync(CancellationToken ct = default) => await _users.CountAsync(ct) > 0;

    /// <summary>최초 실행에서만 관리자 계정을 만든다. 이미 계정이 있으면 거부(우회 방지).</summary>
    public async Task<UserAccount> CreateFirstAdminAsync(string loginId, string password, CancellationToken ct = default)
    {
        loginId = (loginId ?? "").Trim();
        Require(PasswordPolicy.ValidateLoginId(loginId));
        Require(PasswordPolicy.ValidatePassword(loginId, password));
        var created = await _users.CreateFirstAdminAsync(loginId, PasswordHasher.Hash(password), ct)
            ?? throw new InvalidOperationException("이미 계정이 있어 최초 관리자를 만들 수 없습니다.");
        await _audit.WriteAsync(created, loginId, AuditActions.UserCreate, $"최초 관리자 {created.LoginId}");
        return created;
    }

    public async Task<LoginResult> LoginAsync(string loginId, string password, CancellationToken ct = default)
    {
        loginId = (loginId ?? "").Trim();
        password ??= "";
        var rec = loginId.Length == 0 ? null : await _users.FindByLoginIdAsync(loginId, ct);
        var now = _now();

        if (rec is null)
        {
            PasswordHasher.BurnTime(password);
            await _audit.WriteAsync(null, loginId, AuditActions.LoginFail, "알 수 없는 아이디");
            return new LoginResult(false, null, GenericFailure);
        }

        if (rec.LockedUntilUtc is { } until && until > now)
        {
            await _audit.WriteAsync(rec.Account, loginId, AuditActions.LoginLocked, $"잠금 중(해제 {until:O})");
            int minutes = (int)Math.Ceiling((until - now).TotalMinutes);
            return new LoginResult(false, null, $"로그인 시도가 너무 많아 잠겼습니다. 약 {minutes}분 뒤 다시 시도하세요.");
        }

        bool ok = PasswordHasher.Verify(password, rec.PasswordHash);
        if (!rec.Account.Active)
        {
            // 비활성 계정은 비밀번호가 맞아도 막되, 메시지는 일반 실패와 같다
            await _audit.WriteAsync(rec.Account, loginId, AuditActions.LoginFail, ok ? "비활성 계정" : "비밀번호 불일치(비활성 계정)");
            return new LoginResult(false, null, GenericFailure);
        }

        if (ok)
        {
            if (rec.FailedCount > 0 || rec.LockedUntilUtc is not null)
                await _users.SetFailureStateAsync(rec.Account.Id, 0, null, ct);
            await _audit.WriteAsync(rec.Account, loginId, AuditActions.LoginSuccess);
            return new LoginResult(true, rec.Account, "");
        }

        int failed = rec.FailedCount + 1;
        DateTime? lockUntil = failed >= MaxFailures ? now + LockDuration : null;
        await _users.SetFailureStateAsync(rec.Account.Id, failed, lockUntil, ct);
        await _audit.WriteAsync(rec.Account, loginId, AuditActions.LoginFail, lockUntil is null ? $"비밀번호 불일치({failed}회)" : $"비밀번호 불일치({failed}회) → 잠금");
        return new LoginResult(false, null, lockUntil is null ? GenericFailure
            : $"로그인 시도가 너무 많아 잠겼습니다. 약 {(int)LockDuration.TotalMinutes}분 뒤 다시 시도하세요.");
    }

    public async Task<UserAccount> CreateUserAsync(UserAccount actor, string loginId, string password, Role role, CancellationToken ct = default)
    {
        await RequireAdminAsync(actor, ct);
        loginId = (loginId ?? "").Trim();
        Require(PasswordPolicy.ValidateLoginId(loginId));
        Require(PasswordPolicy.ValidatePassword(loginId, password));
        var created = await _users.CreateAsync(loginId, PasswordHasher.Hash(password), role, ct);
        await _audit.WriteAsync(actor, actor.LoginId, AuditActions.UserCreate, $"{created.LoginId} ({role})");
        return created;
    }

    public async Task SetActiveAsync(UserAccount actor, long targetId, bool active, CancellationToken ct = default)
    {
        await RequireAdminAsync(actor, ct);
        var target = await _users.GetAsync(targetId, ct) ?? throw new InvalidOperationException("계정을 찾을 수 없습니다.");
        if (!active)
        {
            if (targetId == actor.Id) throw new InvalidOperationException("자기 자신은 비활성화할 수 없습니다.");
            if (target.Account.Role == Role.Admin && target.Account.Active && await _users.CountActiveAdminsAsync(ct) <= 1)
                throw new InvalidOperationException("마지막 관리자는 비활성화할 수 없습니다.");
        }
        await _users.SetActiveAsync(targetId, active, ct);
        await _audit.WriteAsync(actor, actor.LoginId, active ? AuditActions.UserActivate : AuditActions.UserDeactivate, target.Account.LoginId);
    }

    public async Task ChangePasswordAsync(UserAccount user, string oldPassword, string newPassword, CancellationToken ct = default)
    {
        var rec = await _users.GetAsync(user.Id, ct) ?? throw new InvalidOperationException("계정을 찾을 수 없습니다.");
        if (!PasswordHasher.Verify(oldPassword ?? "", rec.PasswordHash))
            throw new InvalidOperationException("현재 비밀번호가 올바르지 않습니다.");
        Require(PasswordPolicy.ValidatePassword(user.LoginId, newPassword));
        await _users.SetPasswordHashAsync(user.Id, PasswordHasher.Hash(newPassword), ct);
        await _audit.WriteAsync(user, user.LoginId, AuditActions.PasswordChange);
    }

    /// <summary>관리자가 다른 사용자의 비밀번호를 재설정한다(잠금도 해제).</summary>
    public async Task ResetPasswordAsync(UserAccount actor, long targetId, string newPassword, CancellationToken ct = default)
    {
        await RequireAdminAsync(actor, ct);
        var target = await _users.GetAsync(targetId, ct) ?? throw new InvalidOperationException("계정을 찾을 수 없습니다.");
        Require(PasswordPolicy.ValidatePassword(target.Account.LoginId, newPassword));
        await _users.SetPasswordHashAsync(targetId, PasswordHasher.Hash(newPassword), ct);
        await _users.SetFailureStateAsync(targetId, 0, null, ct);
        await _audit.WriteAsync(actor, actor.LoginId, AuditActions.PasswordReset, target.Account.LoginId);
    }

    /// <summary>세션에 들고 있는 권한이 아니라 저장소의 현재 상태로 확인한다(그 사이 비활성화·강등 대비).</summary>
    private async Task RequireAdminAsync(UserAccount actor, CancellationToken ct)
    {
        var cur = await _users.GetAsync(actor.Id, ct);
        if (cur is null || !cur.Account.Active || cur.Account.Role != Role.Admin)
            throw new UnauthorizedAccessException("관리자 권한이 필요합니다.");
    }

    private static void Require(string? problem)
    {
        if (problem is not null) throw new ArgumentException(problem);
    }
}
