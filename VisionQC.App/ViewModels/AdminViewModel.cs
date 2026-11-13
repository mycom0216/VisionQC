using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisionQC.Core;

namespace VisionQC.App.ViewModels;

public sealed record UserRow(UserAccount Account)
{
    public long Id => Account.Id;
    public string LoginId => Account.LoginId;
    public string RoleText => Account.Role.ToString();
    public string StateText => Account.Active ? "활성" : "비활성";
    public string CreatedText => Account.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}

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

    public ObservableCollection<UserRow> Users { get; } = new();
    public ObservableCollection<AuditRow> Audit { get; } = new();
    public string[] ActionOptions { get; } = new[] { "전체" }.Concat(AuditActions.All).ToArray();

    [ObservableProperty] private UserRow? _selectedUser;
    [ObservableProperty] private string _userMessage = "";
    [ObservableProperty] private string _selectedAction = "전체";
    [ObservableProperty] private string _auditMessage = "";

    public async Task RefreshAllAsync()
    {
        await RefreshUsersAsync();
        await RefreshAuditAsync();
    }

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
