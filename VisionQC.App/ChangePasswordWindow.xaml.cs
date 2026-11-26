// ============================================================================================
// ChangePasswordWindow.xaml.cs — 비밀번호 변경 대화상자(ChangePasswordWindow.xaml)의 코드 비하인드(FR-12).
//
// 메인 창 상단의 "비밀번호 변경" 버튼에서 모달로 띄운다. 현재 비밀번호를 확인한 뒤 새 비밀번호로 바꾼다.
// 길이·아이디 포함 금지 같은 규칙과 현재 비밀번호 검증은 AuthService.ChangePasswordAsync가 한다.
// 이 창은 입력 확인(새 비밀번호 두 칸 일치)과 결과 메시지 표시만 맡는다.
// ============================================================================================
using System.Windows;
using VisionQC.Core;

namespace VisionQC.App;

/// <summary>비밀번호 변경 대화상자. 성공하면 DialogResult=true로 닫힌다.</summary>
public partial class ChangePasswordWindow : Window
{
    // 인증 서비스와, 비밀번호를 바꿀 대상(= 지금 로그인한 본인 계정).
    private readonly AuthService _auth;
    private readonly UserAccount _user;

    public ChangePasswordWindow(AuthService auth, UserAccount user)
    {
        InitializeComponent();
        _auth = auth;
        _user = user;
    }

    /// <summary>"변경" 버튼. 해시 계산이 느리므로 Task.Run으로 백그라운드에서 처리하고, 규칙 위반은 메시지로 보여 준다.</summary>
    private async void Ok_Click(object sender, RoutedEventArgs e)
    {
        MessageText.Text = "";
        if (NewBox.Password != ConfirmBox.Password) { MessageText.Text = "새 비밀번호 확인이 일치하지 않습니다."; return; }
        OkButton.IsEnabled = false;
        try
        {
            await Task.Run(() => _auth.ChangePasswordAsync(_user, OldBox.Password, NewBox.Password));
            DialogResult = true;
        }
        // 입력 규칙 위반(너무 짧음·현재 비밀번호 불일치 등)은 예상된 오류라 메시지 그대로 보여 주고, 그 외는 "오류:"를 붙여 구분한다.
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { MessageText.Text = ex.Message; }
        catch (Exception ex) { MessageText.Text = $"오류: {ex.Message}"; }
        finally { OkButton.IsEnabled = true; }
    }
}
