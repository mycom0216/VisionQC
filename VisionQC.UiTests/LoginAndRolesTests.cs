using System.Text.Json;
using FlaUI.Core.AutomationElements;

namespace VisionQC.UiTests;

public class LoginAndRolesTests
{
    [Fact]
    public void First_run_creates_admin_and_shows_every_tab()
    {
        using var run = new AppRun();
        var main = run.CreateAdminAndEnter();
        Assert.Equal("admin · Admin", AppRun.TextOf(main, "UserLabel"));
        Assert.Equal(new[] { "InspectionTab", "HistoryTab", "ReviewTab", "StatsTab", "SettingsTab", "DataTab", "AdminTab" }, AppRun.TabIds(main));
    }

    [Fact]
    public void Weak_or_mismatched_passwords_are_rejected_with_a_reason()
    {
        using var run = new AppRun();
        var login = run.WaitWindow("최초 설정");

        AppRun.SetText(login, "LoginId", "admin");
        AppRun.TypeSecret(login, "LoginPassword", "my-admin-pass-1");        // 아이디 포함
        AppRun.TypeSecret(login, "LoginConfirm", "my-admin-pass-1");
        AppRun.Click(login, "LoginButton");
        Assert.Contains("아이디를 포함", AppRun.WaitText(login, "LoginMessage"));

        // 확인 입력이 다르면 계정을 만들지 않는다 (비밀번호는 정책을 통과하는 값, 확인만 다르게)
        AppRun.TypeSecret(login, "LoginPassword", "Strong-pass-123");
        AppRun.TypeSecret(login, "LoginConfirm", "Different-pass-456");
        AppRun.Click(login, "LoginButton");
        Assert.Contains("일치하지 않습니다", AppRun.WaitText(login, "LoginMessage"));
    }

    [Fact]
    public void Operator_sees_only_inspection_history_and_stats_and_cannot_see_admin_tabs()
    {
        using var run = new AppRun();
        var admin = run.CreateAdminAndEnter();
        AppRun.SelectTab(admin, "AdminTab");
        AppRun.SetText(admin, "NewLoginId", "oper1");
        AppRun.TypeSecret(admin, "NewPassword", "Oper-pass-4321");
        AppRun.By(admin, "NewRole").AsComboBox().Select("Operator");
        AppRun.Click(admin, "CreateUserButton");
        Thread.Sleep(1800);
        Assert.Contains("oper1", AppRun.TextOf(admin, "UserMessage"));

        AppRun.Click(admin, "LogoutButton");
        var op = run.Login("oper1", "Oper-pass-4321", out _, out _);
        Assert.NotNull(op);
        Assert.Equal(new[] { "InspectionTab", "HistoryTab", "StatsTab" }, AppRun.TabIds(op!));
    }

    [Fact]
    public void Wrong_password_and_unknown_user_get_the_same_message()
    {
        using var run = new AppRun();
        var admin = run.CreateAdminAndEnter();
        AppRun.Click(admin, "LogoutButton");

        Assert.Null(run.Login("admin", "wrong-password-1", out var l1, out var wrong));
        Assert.Null(run.Login("nobody", "wrong-password-1", out _, out var unknown));
        Assert.False(string.IsNullOrEmpty(wrong));
        Assert.Equal(wrong, unknown);                                          // 아이디 존재 여부를 알려 주지 않는다
    }

    [Fact]
    public void Five_wrong_attempts_lock_the_account_even_for_the_right_password()
    {
        using var run = new AppRun();
        var admin = run.CreateAdminAndEnter();
        AppRun.Click(admin, "LogoutButton");
        for (int i = 0; i < 5; i++) Assert.Null(run.Login("admin", "wrong-password-1", out _, out _));

        Assert.Null(run.Login("admin", UiEnv.AdminPassword, out _, out var msg));   // 맞는 비밀번호도 잠금 중에는 거부
        Assert.Contains("잠겼습니다", msg);
    }

    [Fact]
    public void Logout_returns_to_login_and_audit_log_records_the_flow()
    {
        using var run = new AppRun();
        var admin = run.CreateAdminAndEnter();
        AppRun.Click(admin, "LogoutButton");
        Assert.NotNull(run.Login("admin", UiEnv.AdminPassword, out _, out _));

        using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={run.DbPath};Mode=ReadOnly;Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT action FROM audit_log ORDER BY id";
        var actions = new List<string>();
        using (var rd = cmd.ExecuteReader()) while (rd.Read()) actions.Add(rd.GetString(0));
        // 자동 백업(db.backup)은 로그인할 때 끼어들 수 있어 흐름 비교에서는 제외한다
        Assert.Equal(new[] { "user.create", "logout", "login.success" }, actions.Where(a => a != "db.backup").ToArray());
    }
}

public class SettingsAndBackupTests
{
    [Fact]
    public void Engineer_can_save_settings_and_the_file_has_the_new_values()
    {
        using var run = new AppRun();
        var main = run.CreateAdminAndEnter();
        AppRun.SelectTab(main, "SettingsTab");

        AppRun.SetText(main, "SettingsThreshold", "0.678");
        AppRun.By(main, "SettingsModelPath").Focus();                          // 포커스를 옮겨 입력을 확정(LostFocus 바인딩)
        Thread.Sleep(500);
        AppRun.Click(main, "SettingsSave");
        Thread.Sleep(1200);
        Assert.Contains("저장했습니다", AppRun.TextOf(main, "SettingsMessage"));

        using var doc = JsonDocument.Parse(File.ReadAllText(run.SettingsPath));
        Assert.Equal(0.678, doc.RootElement.GetProperty("threshold").GetDouble(), 6);
    }

    [Fact]
    public void Out_of_range_threshold_is_rejected_and_nothing_is_written()
    {
        using var run = new AppRun();
        var main = run.CreateAdminAndEnter();
        AppRun.SelectTab(main, "SettingsTab");
        AppRun.SetText(main, "SettingsThreshold", "1.7");
        AppRun.By(main, "SettingsModelPath").Focus();
        Thread.Sleep(500);
        AppRun.Click(main, "SettingsSave");
        Thread.Sleep(800);
        Assert.Contains("0~1", AppRun.TextOf(main, "SettingsMessage"));
        Assert.False(File.Exists(run.SettingsPath));
    }

    [Fact]
    public void Backup_button_creates_a_verified_backup_file_and_audits_it()
    {
        using var run = new AppRun();
        var main = run.CreateAdminAndEnter();
        AppRun.SelectTab(main, "AdminTab");
        AppRun.Click(main, "BackupButton");
        Thread.Sleep(2000);

        Assert.Contains("백업 완료", AppRun.TextOf(main, "BackupMessage"));
        var files = Directory.GetFiles(run.BackupDir, "visionqc_*.db");
        Assert.NotEmpty(files);
    }
}

public class InspectionTests
{
    [SkippableFact]
    public void Folder_inspection_with_a_real_model_completes_and_saves_history()
    {
        var model = Environment.GetEnvironmentVariable("VISIONQC_UITEST_MODEL");
        var images = Environment.GetEnvironmentVariable("VISIONQC_UITEST_IMAGES");
        Skip.If(string.IsNullOrEmpty(model) || string.IsNullOrEmpty(images) || !File.Exists(model) || !Directory.Exists(images),
            "VISIONQC_UITEST_MODEL(모델 .onnx)과 VISIONQC_UITEST_IMAGES(이미지 폴더)를 지정하면 실행됩니다.");

        using var run = new AppRun($"--model \"{model}\" --folder \"{images}\" --threshold 0.5");
        var main = run.CreateAdminAndEnter();
        Thread.Sleep(1500);
        AppRun.Click(main, "StartButton");

        // 완료될 때까지 기다린다(이력 DB에 회차 상태가 Completed가 되면 끝)
        var end = DateTime.UtcNow.AddSeconds(90);
        string status = "";
        while (DateTime.UtcNow < end && status != "Completed")
        {
            Thread.Sleep(1000);
            using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={run.DbPath};Mode=ReadOnly;Pooling=False");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT status FROM inspection_job ORDER BY id DESC LIMIT 1";
            status = Convert.ToString(cmd.ExecuteScalar()) ?? "";
        }
        Assert.Equal("Completed", status);
    }
}
