using System.Windows;
using VisionQC.Core;

namespace VisionQC.App;

/// <summary>
/// 로그인 창. 계정이 하나도 없으면 "최초 관리자 계정 만들기" 모드가 된다.
/// 비밀번호 해시 계산(약 0.2~0.3초)은 UI가 멈추지 않도록 백그라운드에서 한다.
/// </summary>
public partial class LoginWindow : Window
{
    private readonly AuthService _auth;
    private readonly bool _firstRun;

    public UserAccount? User { get; private set; }

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
        Loaded += (_, _) => LoginIdBox.Focus();
    }

    private async void Submit_Click(object sender, RoutedEventArgs e)
    {
        MessageText.Text = "";
        SubmitButton.IsEnabled = false;
        try
        {
            var id = LoginIdBox.Text;
            var pw = PasswordBox.Password;

            if (_firstRun)
            {
                if (pw != ConfirmBox.Password) { MessageText.Text = "비밀번호 확인이 일치하지 않습니다."; return; }
                var created = await Task.Run(() => _auth.CreateFirstAdminAsync(id, pw));
                User = created;
            }
            else
            {
                var r = await Task.Run(() => _auth.LoginAsync(id, pw));
                if (!r.Success) { MessageText.Text = r.Message; PasswordBox.Clear(); PasswordBox.Focus(); return; }
                User = r.User;
            }
            DialogResult = true;
        }
        catch (ArgumentException ex) { MessageText.Text = ex.Message; }          // 아이디·비밀번호 규칙 위반
        catch (InvalidOperationException ex) { MessageText.Text = ex.Message; }  // 이미 계정이 있음 등
        catch (Exception ex) { MessageText.Text = $"오류: {ex.Message}"; }
        finally { SubmitButton.IsEnabled = true; }
    }
}
