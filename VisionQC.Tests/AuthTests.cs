using VisionQC.Core;

namespace VisionQC.Tests;

internal sealed class FakeUsers : IUserRepository
{
    private readonly List<UserRecord> _rows = new();
    private long _next = 1;
    public Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    public Task<int> CountAsync(CancellationToken ct = default) => Task.FromResult(_rows.Count);
    public Task<int> CountActiveAdminsAsync(CancellationToken ct = default)
        => Task.FromResult(_rows.Count(r => r.Account.Role == Role.Admin && r.Account.Active));
    public Task<UserRecord?> FindByLoginIdAsync(string loginId, CancellationToken ct = default)
        => Task.FromResult(_rows.FirstOrDefault(r => string.Equals(r.Account.LoginId, loginId, StringComparison.OrdinalIgnoreCase)));
    public Task<UserRecord?> GetAsync(long id, CancellationToken ct = default)
        => Task.FromResult(_rows.FirstOrDefault(r => r.Account.Id == id));

    public Task<UserAccount?> CreateFirstAdminAsync(string loginId, string passwordHash, CancellationToken ct = default)
        => _rows.Count > 0 ? Task.FromResult<UserAccount?>(null) : CreateAsync(loginId, passwordHash, Role.Admin, ct).ContinueWith(t => (UserAccount?)t.Result);

    public Task<UserAccount> CreateAsync(string loginId, string passwordHash, Role role, CancellationToken ct = default)
    {
        if (_rows.Any(r => string.Equals(r.Account.LoginId, loginId, StringComparison.OrdinalIgnoreCase)))
            throw new DuplicateUserException(loginId);
        var acc = new UserAccount(_next++, loginId, role, true, Now());
        _rows.Add(new UserRecord(acc, passwordHash, 0, null));
        return Task.FromResult(acc);
    }

    public Task<IReadOnlyList<UserAccount>> ListAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<UserAccount>>(_rows.Select(r => r.Account).ToList());

    private void Update(long id, Func<UserRecord, UserRecord> f)
    {
        int i = _rows.FindIndex(r => r.Account.Id == id);
        _rows[i] = f(_rows[i]);
    }
    public Task SetActiveAsync(long id, bool active, CancellationToken ct = default)
    { Update(id, r => r with { Account = r.Account with { Active = active } }); return Task.CompletedTask; }
    public Task SetFailureStateAsync(long id, int failedCount, DateTime? lockedUntilUtc, CancellationToken ct = default)
    { Update(id, r => r with { FailedCount = failedCount, LockedUntilUtc = lockedUntilUtc }); return Task.CompletedTask; }
    public Task SetPasswordHashAsync(long id, string passwordHash, CancellationToken ct = default)
    { Update(id, r => r with { PasswordHash = passwordHash }); return Task.CompletedTask; }
}

internal sealed class FakeAudit : IAuditRepository
{
    public List<AuditEntry> Entries { get; } = new();
    public bool Fail { get; set; }
    public Task AppendAsync(AuditEntry entry, CancellationToken ct = default)
    {
        if (Fail) throw new IOException("audit db down");
        Entries.Add(entry); return Task.CompletedTask;
    }
    public Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditFilter filter, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AuditEntry>>(Entries);
}

public class PasswordTests
{
    [Fact]
    public void Hash_verify_roundtrip_and_wrong_password()
    {
        var h = PasswordHasher.Hash("correct horse battery");
        Assert.StartsWith("PBKDF2-SHA256$600000$", h);
        Assert.True(PasswordHasher.Verify("correct horse battery", h));
        Assert.False(PasswordHasher.Verify("correct horse batterY", h));
        Assert.False(PasswordHasher.Verify("", h));
    }

    [Fact]
    public void Same_password_gives_different_hashes_because_of_salt()
        => Assert.NotEqual(PasswordHasher.Hash("same-password-1"), PasswordHasher.Hash("same-password-1"));

    [Fact]
    public void Hash_does_not_contain_the_password()
        => Assert.DoesNotContain("secret-pass-123", PasswordHasher.Hash("secret-pass-123"));

    [Theory]
    [InlineData("")]
    [InlineData("plaintext")]
    [InlineData("PBKDF2-SHA256$abc$AAAA$AAAA")]
    [InlineData("PBKDF2-SHA256$1000$!!!notbase64$AAAA")]
    [InlineData("OTHER$1000$AAAA$AAAA")]
    public void Malformed_stored_value_is_a_mismatch_not_an_exception(string stored)
        => Assert.False(PasswordHasher.Verify("whatever-pass", stored));

    [Fact]
    public void Korean_password_works()
    {
        var h = PasswordHasher.Hash("비밀번호-테스트-1234");
        Assert.True(PasswordHasher.Verify("비밀번호-테스트-1234", h));
        Assert.False(PasswordHasher.Verify("비밀번호-테스트-1235", h));
    }

    [Theory]
    [InlineData("kim", null)]
    [InlineData("kim.lee-01_x", null)]
    [InlineData("ab", "아이디는")]
    [InlineData("has space", "아이디는")]
    [InlineData("한글아이디", "아이디는")]
    [InlineData("", "아이디는")]
    public void Login_id_rule(string id, string? problem)
        => Assert.Equal(problem is null, PasswordPolicy.ValidateLoginId(id) is null);

    [Theory]
    [InlineData("kim", "short1", false)]
    [InlineData("kim", "longenough1", true)]
    [InlineData("kim", "my-KIM-pass-1", false)]      // 아이디 포함 금지(대소문자 무시)
    [InlineData("kim", "", false)]
    public void Password_policy(string id, string pw, bool ok)
        => Assert.Equal(ok, PasswordPolicy.ValidatePassword(id, pw) is null);

    [Fact]
    public void Permission_matrix()
    {
        foreach (var p in new[] { Permission.RunInspection, Permission.ViewHistory, Permission.ViewStats })
            foreach (var r in Enum.GetValues<Role>()) Assert.True(Permissions.Has(r, p));

        Assert.False(Permissions.Has(Role.Operator, Permission.ReviewResults));
        Assert.False(Permissions.Has(Role.Operator, Permission.ChangeSettings));
        Assert.True(Permissions.Has(Role.Engineer, Permission.ReviewResults));
        Assert.True(Permissions.Has(Role.Engineer, Permission.ChangeSettings));
        Assert.False(Permissions.Has(Role.Engineer, Permission.ManageUsers));
        Assert.False(Permissions.Has(Role.Engineer, Permission.ViewAudit));
        Assert.True(Permissions.Has(Role.Admin, Permission.ManageUsers));
        Assert.True(Permissions.Has(Role.Admin, Permission.ViewAudit));
    }
}

public class AuthServiceTests
{
    private const string Pw = "good-password-1";

    private static (AuthService svc, FakeUsers users, FakeAudit audit, Clock clock) Make()
    {
        var users = new FakeUsers(); var audit = new FakeAudit(); var clock = new Clock();
        return (new AuthService(users, new AuditLogger(audit, clock.Now), clock.Now), users, audit, clock);
    }

    private sealed class Clock
    {
        public DateTime T = new(2026, 11, 13, 6, 0, 0, DateTimeKind.Utc);
        public DateTime Now() => T;
    }

    [Fact]
    public async Task First_admin_only_when_no_users_exist()
    {
        var (svc, _, _, _) = Make();
        Assert.False(await svc.HasAnyUserAsync());
        var admin = await svc.CreateFirstAdminAsync("admin", Pw);
        Assert.Equal(Role.Admin, admin.Role);
        Assert.True(await svc.HasAnyUserAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateFirstAdminAsync("other", Pw));   // 우회 불가
    }

    [Fact]
    public async Task First_admin_validates_login_id_and_password()
    {
        var (svc, _, _, _) = Make();
        await Assert.ThrowsAsync<ArgumentException>(() => svc.CreateFirstAdminAsync("a", Pw));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.CreateFirstAdminAsync("admin", "short"));
        Assert.False(await svc.HasAnyUserAsync());
    }

    [Fact]
    public async Task Login_success_and_audit()
    {
        var (svc, _, audit, _) = Make();
        await svc.CreateFirstAdminAsync("admin", Pw);
        var r = await svc.LoginAsync("ADMIN", Pw);               // 아이디 대소문자 무시
        Assert.True(r.Success);
        Assert.Equal("admin", r.User!.LoginId);
        Assert.Contains(audit.Entries, e => e.Action == AuditActions.LoginSuccess && e.LoginId == "admin");
    }

    [Fact]
    public async Task Wrong_password_and_unknown_user_give_the_same_message()
    {
        var (svc, _, audit, _) = Make();
        await svc.CreateFirstAdminAsync("admin", Pw);
        var bad = await svc.LoginAsync("admin", "wrong-password-1");
        var unknown = await svc.LoginAsync("nobody", "wrong-password-1");
        Assert.False(bad.Success);
        Assert.False(unknown.Success);
        Assert.Equal(bad.Message, unknown.Message);               // 아이디 존재 여부를 알려 주지 않는다
        Assert.Equal(AuthService.GenericFailure, bad.Message);
        Assert.Equal(2, audit.Entries.Count(e => e.Action == AuditActions.LoginFail));
    }

    [Fact]
    public async Task Audit_never_contains_the_password()
    {
        var (svc, _, audit, _) = Make();
        await svc.CreateFirstAdminAsync("admin", Pw);
        await svc.LoginAsync("admin", "wrong-secret-xyz1");
        await svc.LoginAsync("admin", Pw);
        Assert.DoesNotContain(audit.Entries, e => (e.Detail ?? "").Contains("secret") || (e.Detail ?? "").Contains(Pw));
    }

    [Fact]
    public async Task Five_failures_lock_then_correct_password_is_refused_until_expiry()
    {
        var (svc, users, audit, clock) = Make();
        await svc.CreateFirstAdminAsync("admin", Pw);
        for (int i = 0; i < AuthService.MaxFailures; i++)
            Assert.False((await svc.LoginAsync("admin", "wrong-password-1")).Success);

        var locked = await svc.LoginAsync("admin", Pw);           // 맞는 비밀번호여도 잠금 중이면 거부
        Assert.False(locked.Success);
        Assert.Contains("잠겼습니다", locked.Message);
        Assert.Contains(audit.Entries, e => e.Action == AuditActions.LoginLocked);

        clock.T += AuthService.LockDuration + TimeSpan.FromSeconds(1);
        var after = await svc.LoginAsync("admin", Pw);
        Assert.True(after.Success);
        var rec = await users.FindByLoginIdAsync("admin");
        Assert.Equal((0, (DateTime?)null), (rec!.FailedCount, rec.LockedUntilUtc));   // 성공하면 초기화
    }

    [Fact]
    public async Task Failures_below_limit_reset_on_success()
    {
        var (svc, users, _, _) = Make();
        await svc.CreateFirstAdminAsync("admin", Pw);
        for (int i = 0; i < 3; i++) await svc.LoginAsync("admin", "wrong-password-1");
        Assert.Equal(3, (await users.FindByLoginIdAsync("admin"))!.FailedCount);
        Assert.True((await svc.LoginAsync("admin", Pw)).Success);
        Assert.Equal(0, (await users.FindByLoginIdAsync("admin"))!.FailedCount);
    }

    [Fact]
    public async Task Failure_right_after_lock_expiry_locks_again()
    {
        var (svc, _, _, clock) = Make();
        await svc.CreateFirstAdminAsync("admin", Pw);
        for (int i = 0; i < AuthService.MaxFailures; i++) await svc.LoginAsync("admin", "wrong-password-1");
        clock.T += AuthService.LockDuration + TimeSpan.FromSeconds(1);
        var r = await svc.LoginAsync("admin", "wrong-password-1");     // 만료 직후 한 번만 틀려도 다시 잠김
        Assert.Contains("잠겼습니다", r.Message);
    }

    [Fact]
    public async Task Inactive_account_cannot_login_even_with_correct_password_and_message_is_generic()
    {
        var (svc, _, _, _) = Make();
        var admin = await svc.CreateFirstAdminAsync("admin", Pw);
        var op = await svc.CreateUserAsync(admin, "oper", Pw, Role.Operator);
        await svc.SetActiveAsync(admin, op.Id, false);
        var r = await svc.LoginAsync("oper", Pw);
        Assert.False(r.Success);
        Assert.Equal(AuthService.GenericFailure, r.Message);
    }

    [Fact]
    public async Task Only_admin_can_create_users_and_duplicates_are_rejected()
    {
        var (svc, _, audit, _) = Make();
        var admin = await svc.CreateFirstAdminAsync("admin", Pw);
        var eng = await svc.CreateUserAsync(admin, "eng", Pw, Role.Engineer);
        Assert.Equal(Role.Engineer, eng.Role);
        Assert.Contains(audit.Entries, e => e.Action == AuditActions.UserCreate && e.Detail!.Contains("eng"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CreateUserAsync(eng, "x-user", Pw, Role.Operator));
        await Assert.ThrowsAsync<DuplicateUserException>(() => svc.CreateUserAsync(admin, "ENG", Pw, Role.Operator));   // 대소문자 무시
        await Assert.ThrowsAsync<ArgumentException>(() => svc.CreateUserAsync(admin, "ok-user", "short", Role.Operator));
    }

    [Fact]
    public async Task Stale_admin_session_is_rejected_after_deactivation()
    {
        var (svc, users, _, _) = Make();
        var admin = await svc.CreateFirstAdminAsync("admin", Pw);
        var second = await svc.CreateUserAsync(admin, "admin2", Pw, Role.Admin);
        await svc.SetActiveAsync(admin, second.Id, false);            // admin2를 비활성화
        // admin2의 (오래된) 세션 객체로 시도 → 저장소의 현재 상태로 거부
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CreateUserAsync(second, "later", Pw, Role.Operator));
        Assert.Equal(2, (await users.ListAsync()).Count);
    }

    [Fact]
    public async Task Cannot_deactivate_self_or_last_admin()
    {
        var (svc, _, _, _) = Make();
        var admin = await svc.CreateFirstAdminAsync("admin", Pw);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SetActiveAsync(admin, admin.Id, false));
        Assert.Contains("자기 자신", ex.Message);

        var admin2 = await svc.CreateUserAsync(admin, "admin2", Pw, Role.Admin);
        await svc.SetActiveAsync(admin, admin2.Id, false);             // 다른 관리자는 비활성화 가능
        await svc.SetActiveAsync(admin, admin2.Id, true);              // 다시 활성화
    }

    [Fact]
    public async Task Deactivated_admin_loses_admin_rights_immediately()
    {
        var (svc, _, _, _) = Make();
        var admin = await svc.CreateFirstAdminAsync("admin", Pw);
        var admin2 = await svc.CreateUserAsync(admin, "admin2", Pw, Role.Admin);
        await svc.SetActiveAsync(admin2, admin.Id, false);            // admin2가 admin을 끔
        // 꺼진 admin은 (세션 객체가 남아 있어도) 더 이상 관리 작업을 못 한다 → 활성 관리자는 항상 1명 이상 유지
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.SetActiveAsync(admin, admin2.Id, false));
    }

    [Fact]
    public async Task Change_password_needs_old_password_and_applies_policy()
    {
        var (svc, _, audit, _) = Make();
        var admin = await svc.CreateFirstAdminAsync("admin", Pw);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ChangePasswordAsync(admin, "wrong-old-pass1", "new-password-22"));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.ChangePasswordAsync(admin, Pw, "short"));
        await svc.ChangePasswordAsync(admin, Pw, "new-password-22");

        Assert.False((await svc.LoginAsync("admin", Pw)).Success);
        Assert.True((await svc.LoginAsync("admin", "new-password-22")).Success);
        Assert.Contains(audit.Entries, e => e.Action == AuditActions.PasswordChange);
    }

    [Fact]
    public async Task Admin_reset_unlocks_account()
    {
        var (svc, _, _, _) = Make();
        var admin = await svc.CreateFirstAdminAsync("admin", Pw);
        var op = await svc.CreateUserAsync(admin, "oper", Pw, Role.Operator);
        for (int i = 0; i < AuthService.MaxFailures; i++) await svc.LoginAsync("oper", "wrong-password-1");
        Assert.Contains("잠겼습니다", (await svc.LoginAsync("oper", Pw)).Message);

        await svc.ResetPasswordAsync(admin, op.Id, "reset-password-33");
        Assert.True((await svc.LoginAsync("oper", "reset-password-33")).Success);     // 재설정하면 잠금도 풀림
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.ResetPasswordAsync(op, admin.Id, "hack-password-44"));
    }

    [Fact]
    public async Task Audit_failure_does_not_break_login()
    {
        var (svc, _, audit, _) = Make();
        await svc.CreateFirstAdminAsync("admin", Pw);
        audit.Fail = true;                                            // 감사 DB가 죽어도 로그인은 동작(기록은 실패)
        Assert.True((await svc.LoginAsync("admin", Pw)).Success);
    }
}
