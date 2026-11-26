// ============================================================================
// Auth.cs - 사용자 인증·권한(역할 기반 접근 제어)·감사 로그 로직
//
// 검사 파이프라인 자체와는 별개로, "누가 검사를 돌리고 판정을 고치고 설정을 바꿨는가"를 통제·추적하는 보안 계층이다.
// 구성:
//  - Role/Permission/Permissions : 역할 기반 접근 제어(RBAC, Role-Based Access Control). 역할(운영자/엔지니어/관리자)마다 할 수 있는 일이 다르다.
//  - PasswordHasher/PasswordPolicy : 비밀번호를 평문으로 저장하지 않고(PBKDF2 해시) 정책(길이·아이디 포함 금지)을 검사한다.
//  - AuditLogger/AuditActions : 감사 로그(Audit Log). 로그인·설정 변경·정정 등 중요한 행위를 추가 전용으로 기록해 사후 책임 추적을 가능하게 한다.
//  - AuthService : 로그인(실패 횟수 제한·잠금), 계정 생성/비활성화, 비밀번호 변경/재설정을 한곳에서 처리한다.
// 저장소는 인터페이스(IUserRepository/IAuditRepository)로 분리되어 있어 SQLite 구현은 VisionQC.Data 에 있다.
// ============================================================================
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace VisionQC.Core;

/// <summary>
/// 사용자 역할. 숫자가 클수록 권한이 크다(Permissions.Has 가 "role >= Engineer" 같은 비교를 쓰는 이유).
///  - Operator(운영자): 검사 실행, 이력·통계 조회
///  - Engineer(엔지니어): + 판정 정정, 설정(모델·임계값·ROI) 변경
///  - Admin(관리자): + 계정 관리, 감사 로그 조회
/// </summary>
public enum Role { Operator = 0, Engineer = 1, Admin = 2 }

/// <summary>화면/기능 단위의 권한 종류. 역할과 직접 비교하지 않고 이 값으로 "무엇을 할 수 있는가"를 묻는다(권한 규칙을 한곳에 모으기 위해).</summary>
public enum Permission
{
    RunInspection, ViewHistory, ViewStats,   // 모든 역할
    ReviewResults, ChangeSettings,           // 엔지니어 이상
    ManageUsers, ViewAudit                   // 관리자
}

/// <summary>역할 -> 권한 매핑 규칙. 규칙이 바뀌어도 이 한 함수만 고치면 된다.</summary>
public static class Permissions
{
    /// <summary>해당 역할이 권한 p 를 가지면 true. 목록에 없는 권한은 기본적으로 거부(false)한다(최소 권한 원칙).</summary>
    public static bool Has(Role role, Permission p) => p switch
    {
        Permission.RunInspection or Permission.ViewHistory or Permission.ViewStats => true,
        Permission.ReviewResults or Permission.ChangeSettings => role >= Role.Engineer,
        Permission.ManageUsers or Permission.ViewAudit => role == Role.Admin,
        _ => false
    };
}

/// <summary>화면에 넘겨도 되는 계정 정보(비밀번호 해시 등 민감 정보 없음). Active=false 이면 로그인할 수 없다(삭제 대신 비활성화해 감사 기록을 보존).</summary>
public sealed record UserAccount(long Id, string LoginId, Role Role, bool Active, DateTime CreatedAtUtc);

/// <summary>저장소에서 읽은 계정 + 인증 상태(해시·실패 횟수·잠금). UI에는 UserAccount만 넘긴다.</summary>
public sealed record UserRecord(UserAccount Account, string PasswordHash, int FailedCount, DateTime? LockedUntilUtc);

/// <summary>이미 같은 로그인 아이디가 있을 때 저장소가 던지는 예외.</summary>
public sealed class DuplicateUserException : Exception
{
    public DuplicateUserException(string loginId) : base($"이미 사용 중인 아이디입니다: {loginId}") { }
}

/// <summary>감사 로그 1줄. 시각(UTC), 행위자(UserId는 로그인 전 실패 등에서 null 가능), 행위 종류(Action), 상세 설명.</summary>
public sealed record AuditEntry(long Id, DateTime AtUtc, long? UserId, string LoginId, string Action, string? Detail);

/// <summary>감사 로그 조회 조건(기간·행위 종류·최대 건수).</summary>
public sealed record AuditFilter(DateTime? FromUtc = null, DateTime? ToUtc = null, string? Action = null, int Limit = 500);

/// <summary>감사 로그의 행위 종류 코드 모음. 문자열 오타를 막고 필터 목록(All)을 한곳에서 관리한다.</summary>
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

/// <summary>계정 저장소 계약(구현: SQLite). 인증 서비스는 이 인터페이스만 알아 저장 방식에 의존하지 않는다.</summary>
public interface IUserRepository
{
    /// <summary>전체 계정 수(0이면 최초 실행 -> 관리자 생성 화면).</summary>
    Task<int> CountAsync(CancellationToken ct = default);
    /// <summary>활성 관리자 수. 마지막 관리자를 비활성화해 아무도 관리할 수 없게 되는 일을 막는 데 쓴다.</summary>
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
// 해시(hash) = 비밀번호를 되돌릴 수 없는 값으로 바꾼 것. DB가 유출돼도 원래 비밀번호를 알기 어렵다.
// salt = 사용자마다 다른 무작위 값. 같은 비밀번호라도 해시가 달라져 미리 계산한 표(레인보 테이블) 공격을 막는다.
// 반복 횟수(Iterations)가 클수록 한 번 계산이 느려져 무차별 대입 공격 비용이 커진다.
public static class PasswordHasher
{
    // 반복 횟수는 저장 문자열에 함께 들어가므로, 나중에 값을 올려도 예전 해시를 계속 검증할 수 있다.
    public const int Iterations = 600_000;   // OWASP 권장(PBKDF2-HMAC-SHA256)
    private const int SaltBytes = 16, HashBytes = 32;
    private const string Prefix = "PBKDF2-SHA256";

    /// <summary>현재 권장 반복 횟수로 비밀번호를 해시한다. 호출할 때마다 새 salt 를 쓰므로 결과 문자열이 매번 다르다.</summary>
    public static string Hash(string password) => Hash(password, Iterations);

    // 테스트가 반복 횟수를 낮춰 빠르게 돌릴 수 있도록 internal 로 열어 둔 오버로드.
    internal static string Hash(string password, int iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Prefix}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>입력 비밀번호가 저장된 해시와 일치하는지 검사한다. 형식이 이상한 저장값은 예외 없이 false.</summary>
    public static bool Verify(string password, string stored)
    {
        try
        {
            var p = stored.Split('$');
            if (p.Length != 4 || p[0] != Prefix || !int.TryParse(p[1], out var iter) || iter < 1) return false;
            var salt = Convert.FromBase64String(p[2]);
            var expected = Convert.FromBase64String(p[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iter, HashAlgorithmName.SHA256, expected.Length);
            // 고정 시간 비교: 일반 비교는 다른 위치에서 일찍 끝나 응답 시간 차이로 정답을 추측(타이밍 공격)할 수 있어 쓰지 않는다.
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }

    // 존재하지 않는 아이디용 더미 해시. 처음 쓸 때 한 번만 만든다(Lazy).
    private static readonly Lazy<string> Dummy = new(() => Hash("dummy-password-for-timing"));

    /// <summary>없는 아이디로 로그인해도 응답 시간이 같도록 해시 계산을 한 번 수행한다(아이디 존재 여부 추측 방지).</summary>
    public static void BurnTime(string password) => Verify(password, Dummy.Value);
}

/// <summary>아이디·비밀번호 입력 규칙 검사. 문제가 있으면 사용자에게 보여 줄 사유 문자열, 없으면 null 을 돌려준다.</summary>
public static class PasswordPolicy
{
    public const int MinLength = 8, MaxLength = 128;
    // 아이디: 영문·숫자와 _ . - 만 3~32자. 공백·특수문자를 막아 로그/UI 표시와 저장 시 문제가 생기는 것을 줄인다.
    private static readonly Regex LoginIdRule = new(@"^[A-Za-z0-9_.\-]{3,32}$", RegexOptions.Compiled);

    /// <summary>문제가 있으면 사유, 없으면 null.</summary>
    public static string? ValidateLoginId(string? loginId)
        => loginId is not null && LoginIdRule.IsMatch(loginId.Trim())
            ? null : "아이디는 영문·숫자·_ . - 로 3~32자여야 합니다.";

    /// <summary>비밀번호 길이(8~128자)와 "아이디를 포함하지 않음"을 검사한다. 상한은 해시 계산에 쓰는 CPU 를 악용한 공격(아주 긴 입력)을 막기 위함.</summary>
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
    // 시각 공급 함수(테스트에서 가짜 시계 주입 가능). 잠금 해제 시각 계산에 쓴다.
    private readonly Func<DateTime> _now;
    public AuditLogger(IAuditRepository repo, Func<DateTime>? now = null) { _repo = repo; _now = now ?? (() => DateTime.UtcNow); }

    /// <summary>
    /// 감사 로그를 1줄 추가한다.
    /// </summary>
    /// <param name="user">행위자. 로그인 전(알 수 없는 아이디 등)이면 null.</param>
    /// <param name="loginIdFallback">user 가 null 일 때 기록할 입력 아이디.</param>
    /// <param name="action">AuditActions 의 행위 코드.</param>
    /// <param name="detail">선택 상세 설명(비밀번호 같은 비밀 값은 넣지 않는다).</param>
    /// <returns>기록에 성공하면 true, 저장소 오류로 실패하면 false.</returns>
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

/// <summary>로그인 시도 결과. 실패 시 Message 는 사용자에게 보여 줄 문구(일반 실패 문구로 계정 존재 여부를 숨긴다).</summary>
public sealed record LoginResult(bool Success, UserAccount? User, string Message);

/// <summary>
/// 인증·계정 관리 서비스. 보안 규칙:
///  - 비밀번호를 5회 연속 틀리면 10분간 계정 잠금(무차별 대입 방지).
///  - 실패 메시지는 "아이디 또는 비밀번호"로 동일하게 해 아이디 존재 여부를 추측하지 못하게 한다.
///  - 모든 주요 행위는 감사 로그에 남긴다.
/// </summary>
public sealed class AuthService
{
    // 잠금 기준: 연속 실패 5회, 잠금 10분. 너무 엄하면 실수한 직원이 막히고, 너무 느슨하면 대입 공격이 가능해 균형을 둔 값.
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

    /// <summary>계정이 하나라도 있는지. 없으면 앱은 첫 실행으로 보고 관리자 생성 절차를 띄운다.</summary>
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

    /// <summary>
    /// 로그인 처리. 순서: 아이디 조회 -> (없으면 시간 맞추기용 더미 해시 계산 후 실패) -> 잠금 확인 -> 비밀번호 검증
    /// -> 비활성 계정 차단 -> 성공 시 실패 횟수 초기화 / 실패 시 횟수 증가(5회째면 잠금). 모든 분기를 감사 로그에 기록한다.
    /// </summary>
    public async Task<LoginResult> LoginAsync(string loginId, string password, CancellationToken ct = default)
    {
        loginId = (loginId ?? "").Trim();
        password ??= "";
        var rec = loginId.Length == 0 ? null : await _users.FindByLoginIdAsync(loginId, ct);
        var now = _now();

        if (rec is null)
        {
            // 없는 아이디도 해시 계산 시간을 똑같이 소모해, 응답 시간 차이로 "존재하는 아이디"를 알아내는 공격을 막는다.
            PasswordHasher.BurnTime(password);
            await _audit.WriteAsync(null, loginId, AuditActions.LoginFail, "알 수 없는 아이디");
            return new LoginResult(false, null, GenericFailure);
        }

        // 잠금 기간 중이면 비밀번호가 맞아도 거부한다(남은 시간은 분 단위 올림으로 안내).
        if (rec.LockedUntilUtc is { } until && until > now)
        {
            await _audit.WriteAsync(rec.Account, loginId, AuditActions.LoginLocked, $"잠금 중(해제 {until:O})");
            int minutes = (int)Math.Ceiling((until - now).TotalMinutes);
            return new LoginResult(false, null, $"로그인 시도가 너무 많아 잠겼습니다. 약 {minutes}분 뒤 다시 시도하세요.");
        }

        // 비활성 계정이어도 일단 해시 검증을 수행해(시간 동일화) 감사 로그에 원인을 구분해 남긴다.
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

        // 비밀번호 불일치: 실패 횟수를 올리고 한도(MaxFailures)에 도달하면 잠금 해제 시각을 저장한다.
        int failed = rec.FailedCount + 1;
        DateTime? lockUntil = failed >= MaxFailures ? now + LockDuration : null;
        await _users.SetFailureStateAsync(rec.Account.Id, failed, lockUntil, ct);
        await _audit.WriteAsync(rec.Account, loginId, AuditActions.LoginFail, lockUntil is null ? $"비밀번호 불일치({failed}회)" : $"비밀번호 불일치({failed}회) → 잠금");
        return new LoginResult(false, null, lockUntil is null ? GenericFailure
            : $"로그인 시도가 너무 많아 잠겼습니다. 약 {(int)LockDuration.TotalMinutes}분 뒤 다시 시도하세요.");
    }

    /// <summary>관리자가 새 계정을 만든다. 호출자(actor)가 지금도 관리자인지 저장소에서 다시 확인하고, 아이디·비밀번호 규칙을 검사한다.</summary>
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

    /// <summary>계정을 활성/비활성으로 바꾼다(관리자 전용). 자기 자신이나 마지막 활성 관리자는 비활성화할 수 없다(관리 불능 상태 방지).</summary>
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

    /// <summary>본인 비밀번호 변경. 현재 비밀번호를 다시 확인해(자리를 비운 사이 남이 바꾸는 것 방지) 새 비밀번호가 정책에 맞을 때만 바꾼다.</summary>
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

    // 정책 검사 결과(사유 문자열)가 있으면 ArgumentException 으로 바꿔 던진다.
    private static void Require(string? problem)
    {
        if (problem is not null) throw new ArgumentException(problem);
    }
}
