// ============================================================================================
// AdminViewModel.cs — "관리" 탭(AdminTab)의 ViewModel (FR-12, 관리자 전용).
//
// 하는 일: 사용자 목록 조회·생성·활성/비활성 전환·비밀번호 재설정, 감사 로그 조회(동작별 필터), DB 수동 백업.
//   노출하는 명령: RefreshAuditCommand(감사 로그 조회), BackupCommand(DB 백업). 나머지는 코드 비하인드(MainWindow.xaml.cs)가
//   PasswordBox 값을 읽어 이 클래스의 메서드(CreateUserAsync, ToggleActiveAsync, ResetPasswordAsync)를 직접 호출한다
//   — PasswordBox.Password는 보안상 바인딩할 수 없기 때문이다.
//   관리 탭은 ManageUsers 권한이 없으면 아예 보이지 않지만, 실제 권한 검사는 AuthService가 저장소의 "현재" 역할로 다시 한다.
//
// 용어: 감사 로그(audit log) = 누가 언제 무엇을 했는지 남기는 추가 전용 기록. 비활성 계정은 로그인할 수 없다.
// ============================================================================================
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisionQC.Core;

namespace VisionQC.App.ViewModels;

/// <summary>사용자 표의 한 줄. 계정 정보를 화면용 글자(역할·활성 여부·생성 시각, 로컬 시간)로 바꿔 보여 준다.</summary>
public sealed record UserRow(UserAccount Account)
{
    public long Id => Account.Id;
    public string LoginId => Account.LoginId;
    public string RoleText => Account.Role.ToString();
    public string StateText => Account.Active ? "활성" : "비활성";
    public string CreatedText => Account.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}

/// <summary>감사 로그 표의 한 줄(시각·사용자·동작·상세).</summary>
public sealed record AuditRow(AuditEntry Entry)
{
    public string TimeText => Entry.AtUtc.ToLocalTime().ToString("MM-dd HH:mm:ss");
    public string LoginId => Entry.LoginId;
    public string Action => Entry.Action;
    public string Detail => Entry.Detail ?? "";
}

/// <summary>사용자 관리와 감사 로그 조회(관리자). 실제 권한 검사는 AuthService가 저장소의 현재 상태로 다시 한다.</summary>
public partial class AdminViewModel : ObservableObject
{
    private readonly AppServices _svc;
    private readonly Session _session;

    public AdminViewModel(AppServices svc, Session session) { _svc = svc; _session = session; }

    // 표에 바인딩되는 사용자·감사 로그 목록. ActionOptions는 "전체" + 감사 동작 종류(로그인, 설정 변경, 정정, 백업 등)로 만든 필터 항목이다.
    public ObservableCollection<UserRow> Users { get; } = new();
    public ObservableCollection<AuditRow> Audit { get; } = new();
    public string[] ActionOptions { get; } = new[] { "전체" }.Concat(AuditActions.All).ToArray();

    // 선택한 사용자(비활성 전환·비밀번호 재설정의 대상)와 상태 메시지들.
    [ObservableProperty] private UserRow? _selectedUser;
    [ObservableProperty] private string _userMessage = "";
    [ObservableProperty] private string _selectedAction = "전체";
    [ObservableProperty] private string _auditMessage = "";

    /// <summary>관리 탭을 열 때 사용자 목록과 감사 로그를 모두 새로 읽는다.</summary>
    public async Task RefreshAllAsync()
    {
        await RefreshUsersAsync();
        await RefreshAuditAsync();
    }

    /// <summary>사용자 목록을 다시 읽고, 갱신 전에 선택돼 있던 사용자를 유지한다.</summary>
    public async Task RefreshUsersAsync()
    {
        try
        {
            var selectedId = SelectedUser?.Id;
            var list = await _svc.Accounts.ListAsync();
            Users.Clear();
            foreach (var u in list) Users.Add(new UserRow(u));
            SelectedUser = Users.FirstOrDefault(u => u.Id == selectedId);
        }
        catch (Exception ex) { UserMessage = $"사용자 목록 조회 실패: {ex.Message}"; }
    }

    /// <summary>감사 로그 조회(최신 500건, 동작 필터 적용). 감사 로그를 볼 권한(ViewAudit)이 없으면 조회하지 않는다.</summary>
    [RelayCommand]
    public async Task RefreshAuditAsync()
    {
        if (!_session.Can(Permission.ViewAudit)) { AuditMessage = "감사 로그를 볼 권한이 없습니다."; return; }
        try
        {
            var action = SelectedAction == "전체" ? null : SelectedAction;
            var rows = await _svc.Accounts.QueryAsync(new AuditFilter(Action: action, Limit: 500));
            Audit.Clear();
            foreach (var e in rows) Audit.Add(new AuditRow(e));
            AuditMessage = rows.Count >= 500 ? "최신 500건만 표시합니다." : $"{rows.Count}건";
        }
        catch (Exception ex) { AuditMessage = $"감사 로그 조회 실패: {ex.Message}"; }
    }

    [ObservableProperty] private string _backupMessage = "";

    /// <summary>DB를 백업 폴더에 일관된 사본으로 저장하고(무결성 검사 포함) 오래된 백업을 정리한다. 관리자만.</summary>
    [RelayCommand]
    public async Task BackupAsync()
    {
        if (!_session.Can(Permission.ManageUsers)) { BackupMessage = "백업 권한이 없습니다(관리자)."; return; }
        try
        {
            var r = await VisionQC.Data.SqliteBackup.CreateAsync(_svc.DbPath, _svc.BackupDir);
            await _session.AuditAsync(AuditActions.DbBackup, $"{System.IO.Path.GetFileName(r.Path)} ({r.Bytes / 1024:N0} KB)" + (r.Pruned > 0 ? $", 오래된 백업 {r.Pruned}개 정리" : ""));
            BackupMessage = $"백업 완료: {r.Path} ({r.Bytes / 1024:N0} KB, 무결성 검사 통과)" + (r.Pruned > 0 ? $" · 오래된 백업 {r.Pruned}개 정리" : "");
            await RefreshAuditAsync();
        }
        catch (Exception ex) { BackupMessage = $"백업 실패: {ex.Message}"; }
    }

    /// <summary>
    /// 새 사용자를 만든다. 비밀번호 해시 계산이 느려 Task.Run으로 백그라운드에서 실행한다.
    /// 예상된 오류(규칙 위반·아이디 중복·권한 없음)는 메시지로 보여 주고 false, 성공하면 목록을 새로 읽고 true를 돌려준다(호출한 쪽이 입력 칸을 비움).
    /// </summary>
    public async Task<bool> CreateUserAsync(string loginId, string password, Role role)
    {
        try
        {
            await Task.Run(() => _svc.Auth.CreateUserAsync(_session.User, loginId, password, role));
            UserMessage = $"사용자 '{loginId.Trim()}'({role})를 만들었습니다.";
            await RefreshAllAsync();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or DuplicateUserException or UnauthorizedAccessException or InvalidOperationException)
        {
            UserMessage = ex.Message;
            return false;
        }
        catch (Exception ex) { UserMessage = $"오류: {ex.Message}"; return false; }
    }

    /// <summary>선택한 사용자를 활성 ↔ 비활성으로 전환한다(비활성 계정은 로그인 불가). 권한·규칙 검사는 AuthService가 한다.</summary>
    public async Task ToggleActiveAsync()
    {
        if (SelectedUser is not { } u) { UserMessage = "사용자를 먼저 선택하세요."; return; }
        try
        {
            bool target = !u.Account.Active;
            await _svc.Auth.SetActiveAsync(_session.User, u.Id, target);
            UserMessage = $"'{u.LoginId}'을(를) {(target ? "활성화" : "비활성화")}했습니다.";
            await RefreshAllAsync();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        { UserMessage = ex.Message; }
        catch (Exception ex) { UserMessage = $"오류: {ex.Message}"; }
    }

    /// <summary>선택한 사용자의 비밀번호를 관리자가 재설정한다(잠금도 풀림). 성공하면 true — 호출한 쪽이 입력 칸을 비운다.</summary>
    public async Task<bool> ResetPasswordAsync(string newPassword)
    {
        if (SelectedUser is not { } u) { UserMessage = "사용자를 먼저 선택하세요."; return false; }
        try
        {
            await Task.Run(() => _svc.Auth.ResetPasswordAsync(_session.User, u.Id, newPassword));
            UserMessage = $"'{u.LoginId}'의 비밀번호를 재설정했습니다(잠금 해제 포함).";
            await RefreshAuditAsync();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        { UserMessage = ex.Message; return false; }
        catch (Exception ex) { UserMessage = $"오류: {ex.Message}"; return false; }
    }
}
