// ============================================================================================
// DataViewModel.cs — "데이터" 탭(DataTab)의 ViewModel (FR-15, 관리자 전용).
//
// 하는 일: 백업 파일 목록 보기, 선택한 백업으로 DB 복원, 오래된 검사 이력 삭제(정리).
//   두 위험한 동작(복원·삭제)은 "준비/미리보기 → (MainWindow의 확인 창) → 실행" 3단계로 나뉜다. 확인 창은 View(코드 비하인드)가
//   띄우고, 이 ViewModel의 Execute* 메서드는 사용자가 "예"를 눌렀을 때만 호출된다.
//   안전장치: 관리자만 가능, 검사·감시 중에는 불가(Guard), 삭제 전 자동 백업(실패하면 삭제 안 함), 모든 동작을 감사 로그에 기록.
//
// 용어: 복원 = 백업 파일로 현재 DB를 덮어쓰기(직전 DB는 prerestore_visionqc_*.db로 따로 보관). 이력 정리(purge) = 일정 기간보다 오래된
//       검사 결과를 삭제하는 것. 감사 로그와 계정은 지우지 않는다.
// ============================================================================================
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisionQC.Core;
using VisionQC.Data;

namespace VisionQC.App.ViewModels;

/// <summary>백업 목록의 한 줄(파일명·만든 시각·크기를 화면용 글자로 변환).</summary>
public sealed record BackupRow(SqliteBackup.BackupInfo Info)
{
    public string Name => Info.Name;
    public string StampText => Info.StampLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    public string SizeText => $"{Info.Bytes / 1024:N0} KB";
}

/// <summary>데이터 관리(관리자): 백업 목록·복원, 오래된 이력 정리. 위험한 동작은 확인 창을 거친 뒤에만 Execute*가 호출된다.</summary>
public partial class DataViewModel : ObservableObject
{
    private readonly AppServices _svc;
    private readonly Session _session;
    // 검사·감시가 진행 중인지 알려 주는 함수(MainWindow가 InspectionViewModel.IsRunning을 읽는 람다를 넘긴다). 탭끼리 직접 참조하지 않으려는 설계.
    private readonly Func<bool> _isBusy;

    public DataViewModel(AppServices svc, Session session, Func<bool> isInspectionRunning)
    {
        _svc = svc; _session = session; _isBusy = isInspectionRunning;
    }

    /// <summary>백업 폴더의 파일 목록과 선택 항목, 상태 메시지, 이력 정리 보관 일수(기본 365일)와 삭제 대상 미리보기 글자.</summary>
    public ObservableCollection<BackupRow> Backups { get; } = new();
    [ObservableProperty] private BackupRow? _selectedBackup;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _purgeDaysText = "365";
    [ObservableProperty] private string _purgePreview = "";

    /// <summary>백업 폴더를 다시 읽어 목록을 채운다. 선택돼 있던 백업은 이름으로 찾아 유지한다.</summary>
    [RelayCommand]
    public void Refresh()
    {
        var selected = SelectedBackup?.Name;
        Backups.Clear();
        foreach (var b in SqliteBackup.List(_svc.BackupDir)) Backups.Add(new BackupRow(b));
        SelectedBackup = Backups.FirstOrDefault(b => b.Name == selected);
    }

    /// <summary>위험한 동작의 공통 사전 조건: 관리자 권한이 있고, 검사·감시가 돌고 있지 않아야 한다. 안 되면 사유(reason)를 돌려준다.</summary>
    private bool Guard(out string reason)
    {
        reason = "";
        if (!_session.Can(Permission.ManageUsers)) { reason = "관리자만 할 수 있습니다."; return false; }
        if (_isBusy()) { reason = "검사·감시가 진행 중입니다. 먼저 중지하세요."; return false; }
        return true;
    }

    // ── 복원 ──
    /// <summary>복원 전 점검. 복원할 수 있으면 대상 백업을, 아니면 null(Message에 사유).</summary>
    public SqliteBackup.BackupInfo? PrepareRestore()
    {
        if (!Guard(out var why)) { Message = why; return null; }
        if (SelectedBackup is not { } b) { Message = "복원할 백업을 목록에서 선택하세요."; return null; }
        return b.Info;
    }

    /// <summary>백업으로 복원하고 다시 시작을 요청한다. 현재 DB는 prerestore_visionqc_*.db로 먼저 보관된다.</summary>
    public async Task<bool> ExecuteRestoreAsync(SqliteBackup.BackupInfo backup)
    {
        if (!Guard(out var why)) { Message = why; return false; }
        try
        {
            // 복원 중 DB 파일이 바뀌므로 검사가 돌고 있지 않을 때만 여기까지 온다(Guard). 현재 DB는 안전 사본으로 먼저 보관되고, 결과(r)에 그 경로가 담긴다.
            var r = await SqliteBackup.RestoreAsync(backup.Path, _svc.DbPath, _svc.BackupDir);
            // 감사 기록은 "복원된" DB에 남긴다(복원으로 이전 감사 기록이 되돌려졌으므로, 복원했다는 사실을 새로 적는다)
            await _session.AuditAsync(AuditActions.DbRestore, $"{backup.Name} 로 복원 · 복원 직전 DB 보관: {System.IO.Path.GetFileName(r.SafetyCopyPath)}");
            _svc.RestartRequested = true;
            _svc.LogoutNotice = $"{backup.Name}(으)로 복원했습니다. 다시 로그인하세요(복원 시점의 계정으로 로그인됩니다). 복원 직전 DB는 {System.IO.Path.GetFileName(r.SafetyCopyPath)}에 보관했습니다.";
            return true;
        }
        catch (Exception ex)
        {
            Message = $"복원 실패: {ex.Message} (현재 DB는 바뀌지 않았거나 안전 사본이 보관돼 있습니다)";
            return false;
        }
    }

    // ── 이력 정리 ──
    /// <summary>보관 일수 입력을 검사한다. 최소 보관 일수(최근 이력은 지울 수 없음) 이상, 36500일 이하의 정수여야 한다.</summary>
    private bool TryDays(out int days)
    {
        if (int.TryParse(PurgeDaysText, NumberStyles.Integer, CultureInfo.InvariantCulture, out days) && days >= SqliteResultRepository.MinRetentionDays && days <= 36500)
            return true;
        Message = $"보관 일수는 {SqliteResultRepository.MinRetentionDays}일 이상의 정수여야 합니다(최근 {SqliteResultRepository.MinRetentionDays}일 이력은 지울 수 없음).";
        return false;
    }

    /// <summary>삭제 대상 건수를 보여 준다(아무것도 지우지 않음). 지울 게 있으면 (일수, 건수), 없으면 null.</summary>
    [RelayCommand]
    public async Task<(int Days, int Count)?> PreviewPurgeAsync()
    {
        if (!Guard(out var why)) { Message = why; return null; }
        if (!TryDays(out var days)) return null;
        try
        {
            // 기준 시각(UTC)보다 오래된 결과가 삭제 대상. 여기서는 개수만 센다.
            var cutoff = DateTime.UtcNow.AddDays(-days);
            int n = await _svc.Results.CountOlderThanAsync(cutoff);
            PurgePreview = n == 0 ? $"{days}일보다 오래된 검사 결과가 없습니다." : $"{days}일보다 오래된 검사 결과 {n:N0}건이 삭제 대상입니다(불량 목록·정정 포함, 감사 로그·계정은 유지).";
            Message = "";
            return n == 0 ? null : (days, n);
        }
        catch (Exception ex) { Message = $"조회 실패: {ex.Message}"; return null; }
    }

    /// <summary>되돌릴 수 없는 삭제. <b>먼저 백업</b>하고(실패하면 중단), 삭제하고, 감사 로그에 남긴다.</summary>
    public async Task<bool> ExecutePurgeAsync(int days)
    {
        if (!Guard(out var why)) { Message = why; return false; }
        if (days < SqliteResultRepository.MinRetentionDays) { Message = "보관 일수가 너무 짧습니다."; return false; }
        try
        {
            var backup = await SqliteBackup.CreateAsync(_svc.DbPath, _svc.BackupDir);   // 실패하면 여기서 예외 → 삭제하지 않음
            // 백업이 성공해야만 이 줄에 도달한다 → 백업이 안 되면 삭제도 되지 않는다.
            var res = await _svc.Results.PurgeOlderThanAsync(DateTime.UtcNow.AddDays(-days));
            await _session.AuditAsync(AuditActions.HistoryPurge,
                $"{days}일보다 오래된 결과 {res.Results:N0}건·회차 {res.Jobs}개 삭제 · 사전 백업 {System.IO.Path.GetFileName(backup.Path)}");
            Message = $"삭제 완료: 결과 {res.Results:N0}건, 회차 {res.Jobs}개. 삭제 직전 백업: {System.IO.Path.GetFileName(backup.Path)}";
            PurgePreview = "";
            Refresh();
            return true;
        }
        catch (Exception ex)
        {
            Message = $"이력 정리 실패(백업이 실패하면 삭제하지 않습니다): {ex.Message}";
            return false;
        }
    }
}
