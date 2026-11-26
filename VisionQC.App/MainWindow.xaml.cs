// ============================================================================================
// MainWindow.xaml.cs — 메인 창(MainWindow.xaml)의 코드 비하인드. 로그인 후 보이는 유일한 주 화면.
//
// 구조: 이 창은 탭 7개(검사·이력·재판정·통계·설정·데이터·관리)를 가진다. 각 탭은 자기 ViewModel을 DataContext로 갖는다.
//   검사=InspectionViewModel(FR-01/04/05/06/13), 이력=HistoryViewModel(FR-08), 재판정=ReviewViewModel(FR-09),
//   통계=StatsViewModel(FR-10), 설정=SettingsViewModel(FR-11), 데이터=DataViewModel(FR-15), 관리=AdminViewModel(FR-12).
//   이 파일은 그 ViewModel들을 만들어 탭에 연결하고, 화면 전체에 걸친 일을 처리한다:
//     - 역할에 따른 탭 표시/숨김(권한 없는 탭은 안 보임)
//     - 자동 로그아웃(무조작 타이머)과 자동 백업(주기 타이머)
//     - 탭을 선택할 때 해당 화면 데이터 새로 읽기
//     - PasswordBox·확인 창처럼 순수 바인딩만으로 하기 어려운 버튼 이벤트(복원·삭제·로그아웃·사용자 관리)
//
// 용어: 자동 로그아웃 = 마우스·키보드 입력이 설정한 시간 동안 없으면 자동으로 로그아웃(공용 PC 보호). 검사·감시 중에는 만료시키지 않는다.
// ============================================================================================
using System.Windows;
using System.Windows.Controls;
using VisionQC.App.ViewModels;
using VisionQC.Core;

namespace VisionQC.App;

/// <summary>로그인 후의 메인 창. ViewModel 생성·연결, 권한별 탭 표시, 자동 로그아웃·자동 백업 타이머를 맡는다.</summary>
public partial class MainWindow : Window
{
    // 공유 서비스와 로그인 세션.
    private readonly AppServices _svc;
    private readonly Session _session;
    // 탭별 ViewModel. 각 탭의 DataContext로 연결된다(생성자 참고).
    private readonly HistoryViewModel _history;
    private readonly StatsViewModel _stats;
    private readonly ReviewViewModel _review;
    private readonly SettingsViewModel _settings;
    private readonly AdminViewModel _admin;
    private readonly InspectionViewModel _inspection;
    private readonly DataViewModel _data;
    // DispatcherTimer는 UI 스레드에서 Tick이 실행되므로 화면·창 조작을 Dispatcher 호출 없이 안전하게 할 수 있다.
    // _idleTimer: 무조작 시간을 주기적으로 검사, _backupTimer: 자동 백업 주기 도래 여부를 주기적으로 검사.
    private readonly System.Windows.Threading.DispatcherTimer _idleTimer;
    private readonly System.Windows.Threading.DispatcherTimer _backupTimer;
    // 마지막 입력 시각을 기억하고 만료 여부를 판단하는 Core의 도우미. 설정이 바뀌면 새로 만든다(OnSettingsChanged).
    private IdleMonitor _idle;
    // 실행 인자 --autorun/--tab 처리가 한 번만 실행되게 하는 플래그.
    private bool _autoRunDone;

    /// <summary>
    /// 메인 창을 만들고 탭별 ViewModel을 연결한다.
    /// </summary>
    /// <param name="svc">공유 서비스(저장소·설정 등).</param>
    /// <param name="session">로그인한 사용자와 권한.</param>
    public MainWindow(AppServices svc, Session session)
    {
        InitializeComponent();
        _svc = svc;
        _session = session;

        // 자동 로그아웃 준비: 5초마다 타이머가 만료 여부를 확인하고, 전역 입력 훅(PreProcessInput)이 입력이 있을 때마다 Touch()로 시각을 갱신한다.
        // 설정이 바뀌면 SettingsChanged 이벤트로 새 제한 시간을 반영한다.
        // 자동 로그아웃: 마우스·키보드 입력이 없으면 지정 시간 뒤 로그아웃. 검사·감시 중에는 만료시키지 않는다.
        _idle = new IdleMonitor(TimeSpan.FromMinutes(svc.Effective.IdleTimeoutMinutes));
        _idleTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _idleTimer.Tick += IdleTimer_Tick;
        _idleTimer.Start();
        System.Windows.Input.InputManager.Current.PreProcessInput += OnAnyInput;
        svc.SettingsChanged += OnSettingsChanged;
        // 자동 백업: 로그인 직후 한 번, 이후 30분마다 "주기가 지났는지" 확인(주기는 설정 탭, 기본 24시간). 앱이 켜져 있을 때만 동작한다.
        _backupTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        // Tick/Loaded 핸들러를 async 람다로 만들어 백업 중에도 UI가 멈추지 않는다.
        _backupTimer.Tick += async (_, _) => await RunAutoBackupAsync();
        _backupTimer.Start();
        Loaded += async (_, _) => await RunAutoBackupAsync();
        // 창이 닫힐 때 타이머를 멈추고 이벤트 구독을 해제한다. InputManager.Current와 svc는 창보다 오래 사는 객체라, 해제하지 않으면
        // 로그아웃 후 다시 로그인할 때 이전 창이 메모리에 남고 핸들러가 중복 실행된다.
        Closed += (_, _) =>
        {
            _backupTimer.Stop();
            _idleTimer.Stop();
            System.Windows.Input.InputManager.Current.PreProcessInput -= OnAnyInput;
            _svc.SettingsChanged -= OnSettingsChanged;
        };

        // 화면마다 필요한 권한이 다르다. 권한이 없는 탭은 아예 보이지 않는다.
        // (보이지 않아도 각 동작은 서비스 계층에서 다시 검사한다: 정정=Session.Can, 사용자 관리=AuthService가 저장소의 현재 권한으로 확인)
        // 상단에 "아이디 · 역할"을 표시하고 창 제목에도 아이디를 넣는다.
        UserLabel.Text = $"{session.User.LoginId} · {session.User.Role}";
        Title = $"VisionQC - AI 외관검사  [{session.User.LoginId}]";

        // 각 탭에 ViewModel을 DataContext로 연결 — XAML의 {Binding ...}이 이 객체의 속성·명령을 찾는다(MVVM의 View-ViewModel 연결).
        // 대부분의 ViewModel은 Session을 받아 동작 직전에 다시 권한을 확인한다.
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
        // 데이터 탭은 "검사 중인가"를 알아야 하므로(검사 중 복원·삭제 금지) 그 상태를 읽는 함수를 넘겨 준다.
        _data = new DataViewModel(svc, session, () => inspection.IsRunning);
        DataTab.DataContext = _data;

        // 권한별 탭 표시: 데이터·관리 = ManageUsers(관리자), 재판정 = ReviewResults(엔지니어 이상), 설정 = ChangeSettings(엔지니어 이상).
        // 검사·이력·통계 탭은 모든 로그인 사용자에게 보인다.
        SetVisible(DataTab, session.Can(Permission.ManageUsers));
        SetVisible(ReviewTab, session.Can(Permission.ReviewResults));
        SetVisible(SettingsTab, session.Can(Permission.ChangeSettings));
        SetVisible(AdminTab, session.Can(Permission.ManageUsers));

        // 선택적 실행 인자(시연·자동화용): --folder --model --threshold --db --settings --autorun --tab history|review|stats|settings|admin
        // --autorun(창이 뜨면 자동으로 검사 시작), --tab(시작 탭 선택)은 시연·자동화 테스트용이다. 숨겨진 탭(권한 없음)은 선택되지 않는다.
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
                    "history" => HistoryTab, "review" => ReviewTab, "stats" => StatsTab, "settings" => SettingsTab, "admin" => AdminTab, "data" => DataTab, _ => null
                };
                if (target is { Visibility: Visibility.Visible }) Tabs.SelectedItem = target;
            };
    }

    /// <summary>앱 어디서든 마우스·키보드 입력이 들어오면 "마지막 조작 시각"을 갱신한다(자동 로그아웃 타이머가 쓴다).</summary>
    private void OnAnyInput(object sender, System.Windows.Input.PreProcessInputEventArgs e)
    {
        if (e.StagingItem.Input is System.Windows.Input.MouseEventArgs or System.Windows.Input.KeyboardEventArgs) _idle.Touch();
    }

    /// <summary>설정 탭에서 자동 로그아웃 시간을 바꾸면 새 제한 시간으로 모니터를 다시 만든다(이때부터 새로 센다).</summary>
    private void OnSettingsChanged() => _idle = new IdleMonitor(TimeSpan.FromMinutes(_svc.Effective.IdleTimeoutMinutes));

    /// <summary>
    /// 5초마다 호출되어 무조작 시간이 초과됐는지 확인한다. busy 인자로 "검사·감시 중"을 알려 주면 만료되지 않는다(무인 감시가 끊기지 않게).
    /// 만료되면 감사 로그(SessionTimeout)를 남기고, 로그인 창에 보여 줄 사유를 저장한 뒤 창을 닫아 로그인 화면으로 돌아간다.
    /// </summary>
    private async void IdleTimer_Tick(object? sender, EventArgs e)
    {
        if (!_idle.IsExpired(busy: _inspection.IsRunning)) return;
        _idleTimer.Stop();
        await _session.AuditAsync(AuditActions.SessionTimeout, $"무조작 {_svc.Effective.IdleTimeoutMinutes}분");
        _svc.LogoutNotice = $"{_svc.Effective.IdleTimeoutMinutes}분 동안 사용이 없어 자동 로그아웃했습니다. 다시 로그인하세요.";
        _svc.LogoutRequested = true;
        Close();
    }

    /// <summary>탭을 보이거나(Visible) 접는다(Collapsed). 접은 탭은 탭 헤더에도 나타나지 않는다.</summary>
    private static void SetVisible(TabItem tab, bool visible) => tab.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>탭을 선택할 때마다 그 탭의 데이터를 새로 읽는다(이력·통계·재판정·관리 탭은 DB 조회, 데이터 탭은 백업 목록).</summary>
    private async void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // TabControl 안의 ComboBox 등에서 올라온 SelectionChanged는 무시
        // 이 이벤트는 "버블링"되므로 탭 안의 ComboBox·DataGrid의 SelectionChanged도 올라온다. 탭 자체에서 발생한 것만 처리한다.
        if (e.OriginalSource != Tabs) return;
        if (Tabs.SelectedItem == HistoryTab) await _history.RefreshAsync();
        else if (Tabs.SelectedItem == StatsTab) await _stats.RefreshAsync();
        else if (Tabs.SelectedItem == ReviewTab) await _review.RefreshAsync();
        else if (Tabs.SelectedItem == AdminTab) await _admin.RefreshAllAsync();
        else if (Tabs.SelectedItem == DataTab) _data.Refresh();
    }

    /// <summary>주기가 지났으면 자동 백업을 만든다(로그인 직후 1회 + 30분마다). 데이터 탭이 열려 있으면 백업 목록도 새로 고친다.</summary>
    private async Task RunAutoBackupAsync()
    {
        var r = await _svc.RunAutoBackupIfDueAsync(_session);
        if (r is not null && Tabs.SelectedItem == DataTab) _data.Refresh();
    }

    // ── 데이터 탭: 위험한 동작은 확인 창을 거친 뒤에만 실행 ──
    /// <summary>
    /// "선택한 백업으로 복원…" 버튼. 복원은 현재 DB를 되돌리는 위험한 동작이라, 사전 점검(PrepareRestore) → 확인 창 → 실행의 3단계를 거친다.
    /// 성공하면 RestartRequested를 켜고 창을 닫아 로그인 화면으로 돌아간다(복원된 DB의 계정·설정을 새로 읽어야 하므로).
    /// </summary>
    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_data.PrepareRestore() is not { } backup) return;
        var ok = MessageBox.Show(this,
            $"'{backup.Name}'({backup.StampLocal:yyyy-MM-dd HH:mm})으로 복원합니다.\n\n" +
            "• 현재 DB는 prerestore_visionqc_*.db로 먼저 보관됩니다.\n" +
            "• 이 백업 이후의 검사 이력·정정·계정 변경은 되돌려집니다.\n" +
            "• 복원 후 로그인 화면으로 돌아가며, 복원 시점의 계정으로 다시 로그인해야 합니다.\n\n계속할까요?",
            "백업에서 복원", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (ok != MessageBoxResult.Yes) return;
        if (await _data.ExecuteRestoreAsync(backup)) { _svc.RestartRequested = true; Close(); }
    }

    /// <summary>"대상 건수 미리보기" 버튼. 아무것도 지우지 않고 몇 건이 삭제 대상인지만 보여 준다.</summary>
    private async void PurgePreview_Click(object sender, RoutedEventArgs e) => await _data.PreviewPurgeAsync();

    /// <summary>
    /// "삭제…" 버튼. 오래된 검사 이력 삭제는 되돌릴 수 없으므로 미리보기 → 확인 창(기본 선택은 "아니요") → 실행 순서로 진행한다.
    /// 실행 직전의 자동 백업과 감사 로그 기록은 DataViewModel.ExecutePurgeAsync가 한다.
    /// </summary>
    private async void Purge_Click(object sender, RoutedEventArgs e)
    {
        if (await _data.PreviewPurgeAsync() is not { } target) return;       // 대상이 없거나 입력이 잘못되면 여기서 끝
        var ok = MessageBox.Show(this,
            $"{target.Days}일보다 오래된 검사 결과 {target.Count:N0}건을 삭제합니다(불량 목록·정정 포함).\n\n" +
            "• 삭제 직전에 DB를 자동으로 백업합니다(백업이 실패하면 삭제하지 않습니다).\n" +
            "• 삭제는 되돌릴 수 없습니다. 되돌리려면 그 백업으로 복원해야 합니다.\n• 감사 로그와 계정은 유지됩니다.\n\n정말 삭제할까요?",
            "오래된 이력 삭제", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (ok == MessageBoxResult.Yes) await _data.ExecutePurgeAsync(target.Days);
    }

    /// <summary>"로그아웃" 버튼. 감사 로그를 남기고 창을 닫는다. LogoutRequested가 true라 App이 로그인 창을 다시 띄운다.</summary>
    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        await _session.AuditAsync(AuditActions.Logout);
        _svc.LogoutRequested = true;
        Close();
    }

    /// <summary>"비밀번호 변경" 버튼. 비밀번호 변경 대화상자를 이 창의 자식(Owner)으로 띄운다.</summary>
    private void ChangePassword_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ChangePasswordWindow(_svc.Auth, _session.User) { Owner = this };
        if (dlg.ShowDialog() == true)
            MessageBox.Show(this, "비밀번호를 변경했습니다.", "VisionQC", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ── 관리 탭: PasswordBox는 바인딩할 수 없어 코드 비하인드에서 값을 읽고 곧바로 지운다 ──
    /// <summary>"만들기" 버튼(새 사용자). 콤보박스 순서(0=Operator, 1=Engineer, 2=Admin)를 역할로 바꿔 ViewModel에 넘기고, 성공하면 입력 칸을 비운다.</summary>
    private async void CreateUser_Click(object sender, RoutedEventArgs e)
    {
        var role = NewRole.SelectedIndex switch { 1 => Role.Engineer, 2 => Role.Admin, _ => Role.Operator };
        if (await _admin.CreateUserAsync(NewLoginId.Text, NewPassword.Password, role))
        {
            NewLoginId.Clear();
            NewPassword.Clear();
        }
    }

    /// <summary>"활성/비활성 전환" 버튼. 선택한 사용자 계정을 켜거나 끈다(관리자 권한 확인 등 규칙 검사는 AuthService가 다시 한다).</summary>
    private async void ToggleActive_Click(object sender, RoutedEventArgs e) => await _admin.ToggleActiveAsync();

    /// <summary>"비밀번호 재설정" 버튼. 관리자가 선택한 사용자의 비밀번호를 새로 정한다(잠금도 풀림). 성공하면 입력 칸을 비운다.</summary>
    private async void ResetPassword_Click(object sender, RoutedEventArgs e)
    {
        if (await _admin.ResetPasswordAsync(ResetPassword.Password)) ResetPassword.Clear();
    }
}
