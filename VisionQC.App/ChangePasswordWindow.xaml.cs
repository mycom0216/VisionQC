using System.Windows;
using VisionQC.Core;

namespace VisionQC.App;

public partial class ChangePasswordWindow : Window
{
    private readonly AuthService _auth;
    private readonly UserAccount _user;

    public ChangePasswordWindow(AuthService auth, UserAccount user)
    {
        InitializeComponent();
        _auth = auth;
        _user = user;
    }

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
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { MessageText.Text = ex.Message; }
        catch (Exception ex) { MessageText.Text = $"오류: {ex.Message}"; }
        finally { OkButton.IsEnabled = true; }
    }
}
