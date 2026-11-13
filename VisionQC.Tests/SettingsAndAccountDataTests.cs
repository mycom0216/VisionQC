using OpenCvSharp;
using VisionQC.Core;
using VisionQC.Data;
using VisionQC.Imaging;

namespace VisionQC.Tests;

public class RoiTests
{
    [Theory]
    [InlineData(0, 0, 1, 1, true)]
    [InlineData(0.25, 0.25, 0.5, 0.5, true)]
    [InlineData(0.5, 0, 0.5, 1, true)]
    [InlineData(0.6, 0, 0.5, 1, false)]          // X+W > 1
    [InlineData(0, 0, 0.04, 1, false)]           // 폭이 5% 미만
    [InlineData(-0.1, 0, 0.5, 0.5, false)]
    [InlineData(double.NaN, 0, 0.5, 0.5, false)]
    public void Validate(double x, double y, double w, double h, bool ok)
        => Assert.Equal(ok, new Roi(x, y, w, h).Validate() is null);

    [Fact]
    public void ToPixels_stays_inside_image_with_at_least_one_pixel()
    {
        var (x, y, w, h) = new Roi(0.5, 0.5, 0.5, 0.5).ToPixels(101, 51);
        Assert.True(x >= 0 && y >= 0 && w >= 1 && h >= 1);
        Assert.True(x + w <= 101 && y + h <= 51);

        var tiny = new Roi(0.99, 0.99, 0.05, 0.05).ToPixels(10, 10);       // 반올림 때문에 밖으로 나가지 않아야 함
        Assert.True(tiny.X + tiny.W <= 10 && tiny.Y + tiny.H <= 10 && tiny.W >= 1 && tiny.H >= 1);
    }

    private static string FourQuadrantPng(string dir)
    {
        // 왼쪽 위 파랑, 오른쪽 위 초록, 왼쪽 아래 노랑, 오른쪽 아래 빨강 (BGR)
        using var m = new Mat(100, 200, MatType.CV_8UC3, Scalar.Black);
        m[new Rect(0, 0, 100, 50)].SetTo(new Scalar(255, 0, 0));
        m[new Rect(100, 0, 100, 50)].SetTo(new Scalar(0, 255, 0));
        m[new Rect(0, 50, 100, 50)].SetTo(new Scalar(0, 255, 255));
        m[new Rect(100, 50, 100, 50)].SetTo(new Scalar(0, 0, 255));
        var p = System.IO.Path.Combine(dir, "quad.png");
        Cv2.ImWrite(p, m);
        return p;
    }

    private static (float r, float g, float b) CenterRgb(PreprocessedImage img)
    {
        int plane = img.Width * img.Height, c = (img.Height / 2) * img.Width + img.Width / 2;
        return (img.Data[c], img.Data[plane + c], img.Data[2 * plane + c]);
    }

    [Fact]
    public void Preprocessor_crops_to_roi_before_resize()
    {
        using var d = new TempDir();
        var path = FourQuadrantPng(d.Path);
        var pre = new OpenCvPreprocessor(new[] { 0f, 0f, 0f }, new[] { 1f, 1f, 1f });
        var s = new InspectionSettings { InputSize = 8 };

        var full = pre.Process(new ImageFrame(path), s);
        Assert.Equal((200, 100), (full.OriginalWidth, full.OriginalHeight));

        // 오른쪽 아래 사분면만 → 전부 빨강
        var red = CenterRgb(pre.Process(new ImageFrame(path), s with { Roi = new Roi(0.5, 0.5, 0.5, 0.5) }));
        Assert.Equal((1f, 0f, 0f), (red.r, red.g, red.b));

        // 왼쪽 위 사분면만 → 전부 파랑
        var blue = CenterRgb(pre.Process(new ImageFrame(path), s with { Roi = new Roi(0, 0, 0.5, 0.5) }));
        Assert.Equal((0f, 0f, 1f), (blue.r, blue.g, blue.b));
    }

    [Fact]
    public void Preprocessor_without_roi_is_unchanged_and_invalid_roi_is_rejected()
    {
        using var d = new TempDir();
        var path = FourQuadrantPng(d.Path);
        var pre = new OpenCvPreprocessor();
        var a = pre.Process(new ImageFrame(path), new InspectionSettings { InputSize = 8 });
        var b = pre.Process(new ImageFrame(path), new InspectionSettings { InputSize = 8, Roi = new Roi(0, 0, 1, 1) });
        Assert.Equal(a.Data, b.Data);                                     // 전체 영역 ROI = ROI 없음

        Assert.Throws<InvalidOperationException>(() =>
            pre.Process(new ImageFrame(path), new InspectionSettings { Roi = new Roi(0.8, 0, 0.5, 1) }));
    }
}

public class SettingsStoreTests
{
    [Fact]
    public void Missing_file_gives_defaults_without_warning()
    {
        using var d = new TempDir();
        var (s, w) = new SettingsStore(System.IO.Path.Combine(d.Path, "s.json")).Load();
        Assert.Equal(new AppSettings(), s);
        Assert.Null(w);
    }

    [Fact]
    public void Roundtrip_including_korean_path_and_roi()
    {
        using var d = new TempDir();
        var store = new SettingsStore(System.IO.Path.Combine(d.Path, "s.json"));
        var saved = new AppSettings { FolderPath = @"C:\검사\이미지", ModelPath = @"D:\모델\model.onnx", Threshold = 0.678, Roi = new Roi(0.1, 0.2, 0.5, 0.6) };
        store.Save(saved);
        var (loaded, w) = store.Load();
        Assert.Equal(saved, loaded);
        Assert.Null(w);
        Assert.Contains("검사", File.ReadAllText(store.Path));              // \uXXXX로 깨지지 않고 한글 그대로
        Assert.False(File.Exists(store.Path + ".tmp"));                    // 임시 파일이 남지 않는다
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("null")]
    public void Corrupt_file_falls_back_to_defaults_with_warning(string content)
    {
        using var d = new TempDir();
        var p = System.IO.Path.Combine(d.Path, "s.json");
        File.WriteAllText(p, content);
        var (s, w) = new SettingsStore(p).Load();
        Assert.Equal(new AppSettings(), s);
        Assert.NotNull(w);
    }

    [Fact]
    public void Out_of_range_threshold_is_clamped_and_invalid_roi_dropped_with_warning()
    {
        using var d = new TempDir();
        var p = System.IO.Path.Combine(d.Path, "s.json");
        File.WriteAllText(p, """{"threshold": 7.5, "roi": {"x":0.9,"y":0,"w":0.5,"h":1}, "modelPath":"m.onnx"}""");
        var (s, w) = new SettingsStore(p).Load();
        Assert.Equal(1.0, s.Threshold);
        Assert.Null(s.Roi);
        Assert.Equal("m.onnx", s.ModelPath);                               // 나머지 값은 살린다
        Assert.Contains("임계값", w);
        Assert.Contains("ROI", w);
    }

    [Fact]
    public void Save_rejects_invalid_values_and_keeps_existing_file()
    {
        using var d = new TempDir();
        var store = new SettingsStore(System.IO.Path.Combine(d.Path, "s.json"));
        store.Save(new AppSettings { Threshold = 0.3 });
        Assert.Throws<ArgumentException>(() => store.Save(new AppSettings { Threshold = 1.5 }));
        Assert.Throws<ArgumentException>(() => store.Save(new AppSettings { Roi = new Roi(0.9, 0, 0.5, 1) }));
        Assert.Equal(0.3, store.Load().Settings.Threshold);                // 거부된 저장은 기존 파일을 건드리지 않음
    }

    [Fact]
    public void ToInspectionSettings_carries_threshold_and_roi()
    {
        var i = new AppSettings { Threshold = 0.678, Roi = new Roi(0, 0, 0.5, 0.5) }.ToInspectionSettings();
        Assert.Equal(0.678f, i.Threshold);
        Assert.Equal(new Roi(0, 0, 0.5, 0.5), i.Roi);
    }
}

public class AccountRepositoryTests
{
    [Fact]
    public async Task Create_find_case_insensitive_and_duplicate_rejected()
    {
        using var d = new TempDir();
        var repo = new SqliteAccountRepository(System.IO.Path.Combine(d.Path, "t.db"));
        var u = await repo.CreateAsync("Kim", "hash1", Role.Engineer);
        Assert.Equal((Role.Engineer, true), (u.Role, u.Active));

        var found = await repo.FindByLoginIdAsync("kIM");
        Assert.Equal(u.Id, found!.Account.Id);
        Assert.Equal("hash1", found.PasswordHash);
        await Assert.ThrowsAsync<DuplicateUserException>(() => repo.CreateAsync("KIM", "hash2", Role.Operator));
        Assert.Null(await repo.FindByLoginIdAsync("nobody"));
    }

    [Fact]
    public async Task First_admin_is_created_only_once()
    {
        using var d = new TempDir();
        var repo = new SqliteAccountRepository(System.IO.Path.Combine(d.Path, "t.db"));
        var first = await repo.CreateFirstAdminAsync("admin", "h");
        Assert.Equal(Role.Admin, first!.Role);
        Assert.Null(await repo.CreateFirstAdminAsync("admin2", "h"));
        Assert.Equal(1, await repo.CountAsync());
    }

    [Fact]
    public async Task First_admin_race_creates_exactly_one()
    {
        using var d = new TempDir();
        var path = System.IO.Path.Combine(d.Path, "t.db");
        var repo = new SqliteAccountRepository(path);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => repo.CreateFirstAdminAsync($"admin{i}", "h")));
        Assert.Equal(1, results.Count(r => r is not null));
        Assert.Equal(1, await repo.CountAsync());
    }

    [Fact]
    public async Task Failure_state_active_and_password_updates_persist()
    {
        using var d = new TempDir();
        var repo = new SqliteAccountRepository(System.IO.Path.Combine(d.Path, "t.db"));
        var u = await repo.CreateAsync("kim", "h1", Role.Operator);
        var until = new DateTime(2026, 11, 13, 7, 0, 0, DateTimeKind.Utc);

        await repo.SetFailureStateAsync(u.Id, 5, until);
        var r = (await repo.GetAsync(u.Id))!;
        Assert.Equal((5, until), (r.FailedCount, r.LockedUntilUtc));
        await repo.SetFailureStateAsync(u.Id, 0, null);
        Assert.Null((await repo.GetAsync(u.Id))!.LockedUntilUtc);

        await repo.SetActiveAsync(u.Id, false);
        Assert.False((await repo.GetAsync(u.Id))!.Account.Active);
        await repo.SetPasswordHashAsync(u.Id, "h2");
        Assert.Equal("h2", (await repo.GetAsync(u.Id))!.PasswordHash);
        Assert.Equal(0, await repo.CountActiveAdminsAsync());
    }

    [Fact]
    public async Task Audit_append_query_filter_order_and_korean()
    {
        using var d = new TempDir();
        var repo = new SqliteAccountRepository(System.IO.Path.Combine(d.Path, "t.db"));
        var t0 = new DateTime(2026, 11, 13, 1, 0, 0, DateTimeKind.Utc);
        await repo.AppendAsync(new AuditEntry(0, t0, null, "ghost", AuditActions.LoginFail, "알 수 없는 아이디"));   // 사용자 없는 기록도 가능
        await repo.AppendAsync(new AuditEntry(0, t0.AddMinutes(1), 1, "kim", AuditActions.ReviewCorrect, "휘어진 리드"));
        await repo.AppendAsync(new AuditEntry(0, t0.AddMinutes(2), 1, "kim", AuditActions.LoginSuccess, null));

        var all = await repo.QueryAsync(new AuditFilter());
        Assert.Equal(new[] { AuditActions.LoginSuccess, AuditActions.ReviewCorrect, AuditActions.LoginFail }, all.Select(a => a.Action).ToArray());   // 최신순
        Assert.Equal("휘어진 리드", all[1].Detail);
        Assert.Null(all[2].UserId);

        Assert.Single(await repo.QueryAsync(new AuditFilter(Action: AuditActions.ReviewCorrect)));
        Assert.Equal(2, (await repo.QueryAsync(new AuditFilter(FromUtc: t0.AddSeconds(30)))).Count);
        Assert.Single(await repo.QueryAsync(new AuditFilter(Limit: 1)));
    }

    [Fact]
    public async Task Auth_service_end_to_end_on_sqlite()
    {
        using var d = new TempDir();
        var repo = new SqliteAccountRepository(System.IO.Path.Combine(d.Path, "t.db"));
        var svc = new AuthService(repo, new AuditLogger(repo));

        var admin = await svc.CreateFirstAdminAsync("admin", "good-password-1");
        await svc.CreateUserAsync(admin, "operator1", "operator-pass-1", Role.Operator);
        Assert.True((await svc.LoginAsync("Operator1", "operator-pass-1")).Success);
        Assert.False((await svc.LoginAsync("operator1", "bad-password-123")).Success);

        var audit = await repo.QueryAsync(new AuditFilter());
        Assert.Contains(audit, a => a.Action == AuditActions.UserCreate);
        Assert.Contains(audit, a => a.Action == AuditActions.LoginSuccess && a.LoginId == "operator1");
        Assert.Contains(audit, a => a.Action == AuditActions.LoginFail && a.LoginId == "operator1");
    }
}
