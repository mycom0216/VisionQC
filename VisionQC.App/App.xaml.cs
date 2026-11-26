// ============================================================================================
// App.xaml.cs — 애플리케이션 진입점(WPF Application 클래스의 코드 비하인드).
//
// 역할: 프로그램이 시작될 때 가장 먼저 실행되는 곳이다. 아래 순서를 "로그아웃/복원이 일어날 때마다" 반복한다.
//   1) 서비스 묶음(AppServices)을 새로 만든다 — DB 연결, 설정 파일, 인증 서비스 등.
//   2) 로그인 창(LoginWindow)을 띄운다. 계정이 하나도 없으면 "최초 관리자 계정 만들기" 모드가 된다(FR-12).
//   3) 로그인에 성공하면 Session(로그인한 사용자 + 권한)을 만들고 메인 창(MainWindow)을 모달로 띄운다.
//   4) 메인 창이 닫히면: 로그아웃(또는 자동 로그아웃·DB 복원)이면 1번으로 돌아가고, 그냥 닫은 것이면 프로그램을 끝낸다.
//
// MVVM에서의 위치: 이 파일은 View/ViewModel이 아니라 "앱 조립(composition root)"이다.
//   화면(View)과 ViewModel을 만들어 서로 연결하는 일은 MainWindow가 하고, 여기서는 어떤 창을 언제 띄울지만 정한다.
// ============================================================================================
using System.Windows;
using VisionQC.Core;

namespace VisionQC.App;

/// <summary>
/// 앱 전체(WPF Application). App.xaml에서 전역 리소스(변환기)를 선언하고, 여기서는 시작 흐름(로그인 → 메인 → 로그아웃 반복)을 담당한다.
/// </summary>
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
            // 다음 토큰이 "--"로 시작하지 않으면 그것을 값으로 소비(++i로 건너뜀), 아니면 값 없는 플래그("--autorun" 등)로 본다.
            d[key] = i + 1 < argv.Length && !argv[i + 1].StartsWith("--") ? argv[++i] : "";
        }
        return d;
    }

    /// <summary>
    /// WPF가 앱을 시작할 때 호출한다. 일반적인 앱은 App.xaml의 StartupUri로 창 하나를 띄우지만,
    /// 이 앱은 "로그인 창 ↔ 메인 창"이 번갈아 뜨므로 여기서 직접 루프를 돌린다.
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 기본값(OnLastWindowClose)이면 로그인 창이 닫히는 순간 앱이 종료되어 버린다. 그래서 아래의 Shutdown() 호출 때만 종료하도록 바꾼다.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;   // 로그인 창과 메인 창이 번갈아 뜨므로 직접 종료한다
        var args = ParseArgs(e.Args);

        // notice: 다시 뜬 로그인 창에 보여 줄 안내 문구(예: "자동 로그아웃했습니다"). 첫 실행에는 없다.
        string? notice = null;
        while (true)
        {
            // 로그인할 때마다 서비스를 새로 만든다 — DB를 복원한 뒤에는 연결·설정을 반드시 새로 읽어야 한다
            AppServices svc;
            try { svc = new AppServices(args); }
            catch (Exception ex)
            {
                MessageBox.Show($"이력 DB를 열 수 없습니다.\n\n{ex.Message}", "VisionQC", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
                return;
            }

            // 계정이 하나도 없는 DB라면 최초 실행이다 → 로그인 대신 "관리자 계정 만들기" 화면을 보여 준다.
            // 아직 UI 창이 없는 시작 단계라 비동기 대신 GetResult()로 동기 대기해도 화면이 멈추는 문제가 없다.
            bool firstRun;
            try { firstRun = !svc.Auth.HasAnyUserAsync().GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                MessageBox.Show($"계정 정보를 읽을 수 없습니다.\n\n{ex.Message}", "VisionQC", MessageBoxButton.OK, MessageBoxImage.Error);
                break;
            }

            // ShowDialog()는 창이 닫힐 때까지 기다린다. 로그인 성공 시에만 DialogResult가 true가 된다(취소·종료 버튼이면 루프를 끝내고 앱 종료).
            var login = new LoginWindow(svc.Auth, firstRun, notice);
            if (login.ShowDialog() != true || login.User is null) break;

            // 새 로그인 세션 시작: 이전 로그아웃 요청 표시를 지우고, 로그인한 사용자 정보를 담은 Session을 서비스에 연결한다.
            svc.LogoutRequested = false;
            svc.Session = new Session(login.User, svc);
            var main = new MainWindow(svc, svc.Session);
            MainWindow = main;
            main.ShowDialog();

            if (!svc.LogoutRequested && !svc.RestartRequested) break;      // 창을 그냥 닫으면 종료, 로그아웃·복원이면 로그인 창으로
            notice = svc.LogoutNotice ?? "로그아웃했습니다. 다시 로그인하세요.";
        }
        Shutdown();
    }
}
