using System.Windows;
using System.Windows.Controls;
using VisionQC.App.ViewModels;
using VisionQC.Core;

namespace VisionQC.App;

public partial class MainWindow : Window
{
    private readonly AppServices _svc;
    private readonly Session _session;
    private readonly HistoryViewModel _history;
    private readonly StatsViewModel _stats;
    private readonly ReviewViewModel _review;
    private readonly SettingsViewModel _settings;
    private readonly AdminViewModel _admin;
    private readonly InspectionViewModel _inspection;
    private readonly System.Windows.Threading.DispatcherTimer _idleTimer;
    private IdleMonitor _idle;
    private bool _autoRunDone;

    public MainWindow(AppServices svc, Session session)
    {
        InitializeComponent();
        _svc = svc;
        _session = session;

        // 자동 로그아웃: 마우스·키보드 입력이 없으면 지정 시간 뒤 로그아웃. 검사·감시 중에는 만료시키지 않는다.
        _idle = new IdleMonitor(TimeSpan.FromMinutes(svc.Effective.IdleTimeoutMinutes));
        _idleTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _idleTimer.Tick += IdleTimer_Tick;
        _idleTimer.Start();
        System.Windows.Input.InputManager.Current.PreProcessInput += OnAnyInput;
        svc.SettingsChanged += OnSettingsChanged;
        Closed += (_, _) =>
        {
            _idleTimer.Stop();
            System.Windows.Input.InputManager.Current.PreProcessInput -= OnAnyInput;
            _svc.SettingsChanged -= OnSettingsChanged;
        };

        // 화면마다 필요한 권한이 다르다. 권한이 없는 탭은 아예 보이지 않는다.
        // (보이지 않아도 각 동작은 서비스 계층에서 다시 검사한다: 정정=Session.Can, 사용자 관리=AuthService가 저장소의 현재 권한으로 확인)
        UserLabel.Text = $"{session.User.LoginId} · {session.User.Role}";
        Title = $"VisionQC - AI 외관검사  [{session.User.LoginId}]";

        var inspection = new InspectionViewModel(svc, session);
        _inspection = inspection;
        InspectionTab.DataContext = inspection;
        _history = new HistoryViewModel(svc.Results);
        HistoryTab.DataContext = _history;
        _review = new ReviewViewModel(svc.Results, session);
        ReviewTab.DataContext = _review;
        _stats = new StatsViewModel(svc.Results);
        StatsTab.DataContext = _stats;
        _settings = new SettingsViewModel(svc, session);
        SettingsTab.DataContext = _settings;
        _admin = new AdminViewModel(svc, session);
        AdminTab.DataContext = _admin;

        SetVisible(ReviewTab, session.Can(Permission.ReviewResults));
        SetVisible(SettingsTab, session.Can(Permission.ChangeSettings));
        SetVisible(AdminTab, session.Can(Permission.ManageUsers));

        // 선택적 실행 인자(시연·자동화용): --folder --model --threshold --db --settings --autorun --tab history|review|stats|settings|admin
        var startTab = svc.Args.GetValueOrDefault("tab");
        var autorun = svc.Args.ContainsKey("autorun");
        if (autorun || startTab is not null)
            Loaded += async (_, _) =>
            {
                // Loaded는 창이 비주얼 트리에 다시 붙을 때 또 발생할 수 있어 한 번만 실행한다.
                if (_autoRunDone) return;
                _autoRunDone = true;
                if (autorun) await inspection.StartCommand.ExecuteAsync(null);
                TabItem? target = startTab switch
                {
                    "history" => HistoryTab, "review" => ReviewTab, "stats" => StatsTab, "settings" => SettingsTab, "admin" => AdminTab, _ => null
                };
                if (target is { Visibility: Visibility.Visible }) Tabs.SelectedItem = target;
            };
    }

    private void OnAnyInput(object sender, System.Windows.Input.PreProcessInputEventArgs e)
    {
        if (e.StagingItem.Input is System.Windows.Input.MouseEventArgs or System.Windows.Input.KeyboardEventArgs) _idle.Touch();
    }

    private void OnSettingsChanged() => _idle = new IdleMonitor(TimeSpan.FromMinutes(_svc.Effective.IdleTimeoutMinutes));

    private async void IdleTimer_Tick(object? sender, EventArgs e)
    {
        if (!_idle.IsExpired(busy: _inspection.IsRunning)) return;
        _idleTimer.Stop();
        await _session.AuditAsync(AuditActions.SessionTimeout, $"무조작 {_svc.Effective.IdleTimeoutMinutes}분");
        _svc.LogoutNotice = $"{_svc.Effective.IdleTimeoutMinutes}분 동안 사용이 없어 자동 로그아웃했습니다. 다시 로그인하세요.";
        _svc.LogoutRequested = true;
        Close();
    }

    private static void SetVisible(TabItem tab, bool visible) => tab.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private async void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // TabControl 안의 ComboBox 등에서 올라온 SelectionChanged는 무시
        if (e.OriginalSource != Tabs) return;
        if (Tabs.SelectedItem == HistoryTab) await _history.RefreshAsync();
        else if (Tabs.SelectedItem == StatsTab) await _stats.RefreshAsync();
        else if (Tabs.SelectedItem == ReviewTab) await _review.RefreshAsync();
        else if (Tabs.SelectedItem == AdminTab) await _admin.RefreshAllAsync();
    }

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        await _session.AuditAsync(AuditActions.Logout);
        _svc.LogoutRequested = true;
        Close();
    }

    private void ChangePassword_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ChangePasswordWindow(_svc.Auth, _session.User) { Owner = this };
        if (dlg.ShowDialog() == true)
            MessageBox.Show(this, "비밀번호를 변경했습니다.", "VisionQC", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ── 관리 탭: PasswordBox는 바인딩할 수 없어 코드 비하인드에서 값을 읽고 곧바로 지운다 ──
    private async void CreateUser_Click(object sender, RoutedEventArgs e)
    {
        var role = NewRole.SelectedIndex switch { 1 => Role.Engineer, 2 => Role.Admin, _ => Role.Operator };
        if (await _admin.CreateUserAsync(NewLoginId.Text, NewPassword.Password, role))
        {
            NewLoginId.Clear();
            NewPassword.Clear();
        }
    }

    private async void ToggleActive_Click(object sender, RoutedEventArgs e) => await _admin.ToggleActiveAsync();

    private async void ResetPassword_Click(object sender, RoutedEventArgs e)
    {
        if (await _admin.ResetPasswordAsync(ResetPassword.Password)) ResetPassword.Clear();
    }
}
