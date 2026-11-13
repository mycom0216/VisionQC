using System.Windows;
using VisionQC.Core;

namespace VisionQC.App;

public partial class App : Application
{
    /// <summary>실행 인자: --key value 또는 --flag. (비밀번호는 인자로 받지 않는다 — 프로세스 목록에 노출되므로)</summary>
    internal static Dictionary<string, string> ParseArgs(string[] argv)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < argv.Length; i++)
        {
            if (!argv[i].StartsWith("--")) continue;
            var key = argv[i][2..];
            d[key] = i + 1 < argv.Length && !argv[i + 1].StartsWith("--") ? argv[++i] : "";
        }
        return d;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;   // 로그인 창과 메인 창이 번갈아 뜨므로 직접 종료한다

        AppServices svc;
        try { svc = new AppServices(ParseArgs(e.Args)); }
        catch (Exception ex)
        {
            MessageBox.Show($"이력 DB를 열 수 없습니다.\n\n{ex.Message}", "VisionQC", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        string? notice = null;
        while (true)
        {
            bool firstRun;
            try { firstRun = !svc.Auth.HasAnyUserAsync().GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                MessageBox.Show($"계정 정보를 읽을 수 없습니다.\n\n{ex.Message}", "VisionQC", MessageBoxButton.OK, MessageBoxImage.Error);
                break;
            }

            var login = new LoginWindow(svc.Auth, firstRun, notice);
            if (login.ShowDialog() != true || login.User is null) break;

            svc.LogoutRequested = false;
            svc.Session = new Session(login.User, svc);
            var main = new MainWindow(svc, svc.Session);
            MainWindow = main;
            main.ShowDialog();

            if (!svc.LogoutRequested) break;      // 창을 그냥 닫으면 종료, 로그아웃이면 로그인 창으로
            notice = svc.LogoutNotice ?? "로그아웃했습니다. 다시 로그인하세요.";
            svc.LogoutNotice = null;
        }
        Shutdown();
    }
}
