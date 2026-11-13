using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.UIA3;

[assembly: CollectionBehavior(DisableTestParallelization = true)]   // 화면을 쓰는 테스트라 한 번에 하나씩

namespace VisionQC.UiTests;

// ─────────────────────────────────────────────────────────────────────────────
// 화면 자동 테스트 공통 도구 (FlaUI, Windows UI Automation).
//  · 실제 VisionQC.App.exe를 임시 DB·설정·백업 폴더로 띄우므로 내 PC의 진짜 데이터는 건드리지 않는다.
//  · 마우스·키보드를 실제로 쓰므로 실행 중에는 PC를 만지지 말 것(포커스를 뺏기면 실패할 수 있다).
//  · 대화형 데스크톱이 필요하다 — 서비스·헤드리스 CI에서는 실행할 수 없다.
//  · 실행: dotnet test VisionQC.UiTests
//  · 앱 위치: 환경변수 VISIONQC_APP_PATH, 없으면 같은 빌드 구성(Debug/Release)의 VisionQC.App 출력.
//  · 모델이 필요한 검사 테스트는 VISIONQC_UITEST_MODEL, VISIONQC_UITEST_IMAGES(이미지 폴더)가 있을 때만 돈다.
// ─────────────────────────────────────────────────────────────────────────────

internal static class UiEnv
{
    public const string AdminId = "admin";
    public const string AdminPassword = "Strong-pass-123";     // 아이디(admin)를 포함하면 정책에 걸리므로 포함하지 않는다

    public static string AppPath
    {
        get
        {
            if (Environment.GetEnvironmentVariable("VISIONQC_APP_PATH") is { Length: > 0 } env) return env;
            string config = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
                "VisionQC.App", "bin", config, "net10.0-windows", "VisionQC.App.exe"));
        }
    }
}

/// <summary>임시 폴더에서 앱을 실행하고, 끝나면 프로세스를 정리한다.</summary>
internal sealed class AppRun : IDisposable
{
    private readonly UIA3Automation _automation = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "visionqc_ui_" + Guid.NewGuid().ToString("N"));
    private readonly Application _app;

    public string DbPath => Path.Combine(_dir, "visionqc.db");
    public string SettingsPath => Path.Combine(_dir, "settings.json");
    public string BackupDir => Path.Combine(_dir, "backup");
    public string Dir => _dir;

    public AppRun(string? extraArgs = null)
    {
        Directory.CreateDirectory(_dir);
        var exe = UiEnv.AppPath;
        if (!File.Exists(exe)) throw new FileNotFoundException($"앱 실행 파일이 없음: {exe} (먼저 앱을 빌드하세요)");
        var args = $"--db \"{DbPath}\" --settings \"{SettingsPath}\" --backup-dir \"{BackupDir}\" --capture-dir \"{Path.Combine(_dir, "captures")}\" {extraArgs}";
        _app = Application.Launch(new ProcessStartInfo(exe, args) { UseShellExecute = false });
    }

    /// <summary>제목에 titlePart가 들어 있는 이 앱의 창을 기다린다.</summary>
    public Window WaitWindow(string titlePart, int timeoutMs = 20000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < end)
        {
            foreach (var w in _app.GetAllTopLevelWindows(_automation))
                if (w.Title is { } t && t.Contains(titlePart, StringComparison.Ordinal)) return w;
            Thread.Sleep(200);
        }
        throw new TimeoutException($"창을 찾지 못함: '{titlePart}'");
    }

    public static AutomationElement By(AutomationElement root, string automationId)
        => root.FindFirstDescendant(cf => cf.ByAutomationId(automationId))
           ?? throw new InvalidOperationException($"요소를 찾지 못함: {automationId}");

    public static void SetText(AutomationElement root, string id, string text) => By(root, id).AsTextBox().Text = text;

    /// <summary>
    /// PasswordBox는 UIA 값 패턴이 없어 키보드로 입력한다. 창을 전면으로 가져온 뒤, 전체 선택(Ctrl+A)으로 기존 내용을 덮어쓰고 입력한다
    /// (이전 입력이 남아 이어 붙거나, 다른 창이 포커스를 가져간 채 입력되는 것을 막는다).
    /// </summary>
    public static void TypeSecret(AutomationElement root, string id, string text)
    {
        (root as Window)?.SetForeground();
        var el = By(root, id);
        el.Focus();
        Thread.Sleep(200);
        Keyboard.TypeSimultaneously(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL, FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_A);
        Keyboard.Type(text);
        Thread.Sleep(200);
    }

    /// <summary>비어 있지 않은 이름이 나타날 때까지 기다려 요소의 텍스트를 읽는다(비어 있는 TextBlock은 UIA 트리에서 사라진다).</summary>
    public static string WaitText(AutomationElement root, string id, int timeoutMs = 8000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < end)
        {
            var el = root.FindFirstDescendant(cf => cf.ByAutomationId(id));
            if (el is not null && !string.IsNullOrEmpty(el.Name)) return el.Name;
            Thread.Sleep(150);
        }
        return "";
    }

    public static void Click(AutomationElement root, string id) => By(root, id).AsButton().Invoke();

    public static string[] TabIds(Window w)
        => w.FindAllDescendants(cf => cf.ByControlType(ControlType.TabItem)).Select(t => t.AutomationId).ToArray();

    public static void SelectTab(Window w, string tabId)
    {
        By(w, tabId).AsTabItem().Select();
        Thread.Sleep(800);                       // 탭 전환 시 데이터 조회가 끝나기를 기다린다
    }

    public static string TextOf(AutomationElement root, string id) => By(root, id).Name;

    /// <summary>처음 실행 화면에서 관리자를 만들고 메인 창을 돌려준다.</summary>
    public Window CreateAdminAndEnter(string loginId = UiEnv.AdminId, string password = UiEnv.AdminPassword)
    {
        var login = WaitWindow("최초 설정");
        SetText(login, "LoginId", loginId);
        TypeSecret(login, "LoginPassword", password);
        TypeSecret(login, "LoginConfirm", password);
        Click(login, "LoginButton");
        try { return WaitWindow($"[{loginId}]", 15000); }
        catch (TimeoutException)
        {
            // 왜 실패했는지 알 수 있게 화면의 오류 메시지를 함께 알린다
            throw new TimeoutException($"관리자 생성 후 메인 창이 뜨지 않음. 로그인 창 메시지: '{WaitText(login, "LoginMessage", 500)}'");
        }
    }

    /// <summary>로그인 창에서 로그인한다(성공하면 메인 창을, 실패하면 null을 돌려주고 메시지는 login.Name으로 읽는다).</summary>
    public Window? Login(string loginId, string password, out Window loginWindow, out string message)
    {
        loginWindow = WaitWindow("VisionQC 로그인");
        SetText(loginWindow, "LoginId", loginId);
        TypeSecret(loginWindow, "LoginPassword", password);
        Click(loginWindow, "LoginButton");

        // 로그인되어 메인 창이 뜨거나, 실패 메시지가 나타날 때까지(최대 15초) 기다린다. PBKDF2 계산은 환경에 따라 시간이 달라 고정 대기를 쓰지 않는다.
        var end = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < end)
        {
            foreach (var w in _app.GetAllTopLevelWindows(_automation))
                if (w.Title is { } t && t.Contains($"[{loginId}]", StringComparison.Ordinal)) { message = ""; return w; }
            var text = loginWindow.FindFirstDescendant(cf => cf.ByAutomationId("LoginMessage"))?.Name;
            if (!string.IsNullOrEmpty(text)) { message = text; return null; }
            Thread.Sleep(150);
        }
        throw new TimeoutException("로그인 결과(메인 창 또는 오류 메시지)가 15초 안에 나타나지 않음");
    }

    public void Dispose()
    {
        try { if (!_app.HasExited) _app.Kill(); } catch { }
        _automation.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }
}
