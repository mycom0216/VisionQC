// ============================================================================================
// LoginWindow.xaml.cs — 로그인 창(LoginWindow.xaml)의 코드 비하인드(FR-12).
//
// 이 창은 ViewModel 없이 코드 비하인드로 만든 단순한 모달 대화상자다(입력 두세 칸이고 PasswordBox는 바인딩이 불가능하기 때문).
// 두 가지 모드: ① 일반 로그인 ② 계정이 하나도 없을 때의 "최초 관리자 계정 만들기"(비밀번호 확인 칸이 보임).
// 성공하면 DialogResult=true와 함께 User 속성에 로그인한 계정이 담겨 App.OnStartup으로 돌아간다.
// 잠금(5회 실패 시 일정 시간)·비밀번호 규칙 등의 검증은 AuthService가 하고, 이 창은 결과 메시지를 보여 주기만 한다.
// ============================================================================================
using System.Windows;
using VisionQC.Core;

namespace VisionQC.App;

/// <summary>
/// 로그인 창. 계정이 하나도 없으면 "최초 관리자 계정 만들기" 모드가 된다.
/// 비밀번호 해시 계산(약 0.2~0.3초)은 UI가 멈추지 않도록 백그라운드에서 한다.
/// </summary>
public partial class LoginWindow : Window
{
    // 인증 서비스(로그인/최초 관리자 생성)와 "최초 실행 모드인가" 플래그.
    private readonly AuthService _auth;
    private readonly bool _firstRun;

    /// <summary>로그인(또는 최초 생성)에 성공한 계정. 호출한 쪽(App)이 창이 true로 닫힌 뒤 읽는다.</summary>
    public UserAccount? User { get; private set; }

    /// <summary>
    /// 로그인 창을 만든다.
    /// </summary>
    /// <param name="auth">인증 서비스.</param>
    /// <param name="firstRun">계정이 하나도 없으면 true — 제목·안내문을 바꾸고 비밀번호 확인 칸을 보인다.</param>
    /// <param name="notice">자동 로그아웃·복원 후처럼 로그인 창에 먼저 보여 줄 안내 문구(선택).</param>
    public LoginWindow(AuthService auth, bool firstRun, string? notice = null)
    {
        InitializeComponent();
        _auth = auth;
        _firstRun = firstRun;
        if (firstRun)
        {
            Title = "VisionQC 최초 설정";
            Heading.Text = "관리자 계정 만들기";
            SubHeading.Text = "처음 실행입니다. 모든 설정과 사용자 관리를 할 관리자 계정을 만드세요. 비밀번호는 8자 이상이며 복구 기능이 없으니 잘 보관하세요.";
            ConfirmPanel.Visibility = Visibility.Visible;
            SubmitButton.Content = "만들기";
        }
        else SubHeading.Text = notice ?? "계정으로 로그인하세요.";
        // 창이 뜨면 바로 아이디 칸에 커서를 둔다.
        Loaded += (_, _) => LoginIdBox.Focus();
    }

    /// <summary>
    /// "로그인"(또는 "만들기") 버튼. async void는 이벤트 핸들러라서 허용되는 형태다.
    /// 처리 중에는 버튼을 잠가 중복 제출을 막고, 끝나면(finally) 다시 푼다.
    /// </summary>
    private async void Submit_Click(object sender, RoutedEventArgs e)
    {
        MessageText.Text = "";
        SubmitButton.IsEnabled = false;
        try
        {
            // PasswordBox.Password는 바인딩할 수 없는 값이라 코드 비하인드에서 직접 읽는다.
            var id = LoginIdBox.Text;
            var pw = PasswordBox.Password;

            if (_firstRun)
            {
                if (pw != ConfirmBox.Password) { MessageText.Text = "비밀번호 확인이 일치하지 않습니다."; return; }
                // 비밀번호 해시(PBKDF2) 계산이 느려서(약 0.2~0.3초) UI 스레드를 막지 않도록 Task.Run으로 백그라운드에서 실행한다.
                var created = await Task.Run(() => _auth.CreateFirstAdminAsync(id, pw));
                User = created;
            }
            else
            {
                // 실패(잘못된 비밀번호·잠금·비활성 계정 등)는 예외가 아니라 결과 객체로 오며, 사유 문구를 그대로 보여 준다.
                var r = await Task.Run(() => _auth.LoginAsync(id, pw));
                if (!r.Success) { MessageText.Text = r.Message; PasswordBox.Clear(); PasswordBox.Focus(); return; }
                User = r.User;
            }
            // true를 대입하면 ShowDialog()가 true를 돌려주며 창이 닫힌다.
            DialogResult = true;
        }
        catch (ArgumentException ex) { MessageText.Text = ex.Message; }          // 아이디·비밀번호 규칙 위반
        catch (InvalidOperationException ex) { MessageText.Text = ex.Message; }  // 이미 계정이 있음 등
        catch (Exception ex) { MessageText.Text = $"오류: {ex.Message}"; }
        finally { SubmitButton.IsEnabled = true; }
    }
}
