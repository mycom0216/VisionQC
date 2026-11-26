// ============================================================================================
// AppServices.cs — 앱 전체가 공유하는 "서비스 묶음"과 로그인 세션(Session).
//
// AppServices: DB 저장소(검사 결과·계정), 감사 로그, 인증, 설정 파일, 폴더 경로(DB·백업·캡처) 등을 한곳에 모아 둔다.
//   ViewModel들은 필요한 것만 여기서 꺼내 쓴다(간단한 수동 의존성 주입). 로그인할 때마다 새로 만들어진다(App.xaml.cs 참고).
//   설정은 두 층이다: Persisted(파일에 저장된 값) / Effective(실행 인자로 덮어쓴 "지금 쓰는 값").
// Session: "누가 로그인했고 무슨 권한이 있는가"를 담는다. 화면의 권한 검사(Can)와 감사 로그 기록(AuditAsync)의 창구다.
//
// 용어: 감사 로그(audit log) = 로그인·설정 변경·정정·백업 같은 중요한 동작을 누가 언제 했는지 남기는 기록(FR-12).
//       역할(Role) = Operator(검사 실행), Engineer(설정·재판정), Admin(사용자·데이터 관리).
// ============================================================================================
using System.IO;
using VisionQC.Core;
using VisionQC.Data;

namespace VisionQC.App;

/// <summary>앱 전체가 공유하는 서비스 묶음. 실행 인자와 설정 파일을 합쳐 "지금 쓰는 설정(Effective)"을 관리한다.</summary>
public sealed class AppServices
{
    /// <summary>실행 인자(--key value). 예: --folder --model --threshold --db --settings --autorun --tab.</summary>
    public IReadOnlyDictionary<string, string> Args { get; }
    /// <summary>검사 결과·정정 이력을 SQLite에 저장·조회하는 저장소(FR-07, FR-08, FR-09).</summary>
    public SqliteResultRepository Results { get; }
    /// <summary>사용자 계정과 감사 로그를 SQLite에 저장·조회하는 저장소(FR-12).</summary>
    public SqliteAccountRepository Accounts { get; }
    /// <summary>감사 로그를 기록하는 도우미.</summary>
    public AuditLogger Audit { get; }
    /// <summary>로그인·비밀번호 변경·사용자 관리(생성/활성 전환/재설정)를 처리하는 서비스. 권한 검사도 여기서 다시 한다.</summary>
    public AuthService Auth { get; }
    /// <summary>설정 파일(settings.json)을 읽고 쓰는 저장소.</summary>
    public SettingsStore SettingsStore { get; }
    /// <summary>SQLite DB 파일 경로(기본 %LOCALAPPDATA%\VisionQC\visionqc.db, 실행 인자 --db).</summary>
    public string DbPath { get; }
    /// <summary>DB 백업 폴더(기본 %LOCALAPPDATA%\VisionQC\backup, 실행 인자 --backup-dir).</summary>
    public string BackupDir { get; }
    /// <summary>카메라·영상에서 뽑은 프레임(PNG)을 저장하는 폴더(기본 %LOCALAPPDATA%\VisionQC\captures, 실행 인자 --capture-dir). 자동으로 지우지 않는다.</summary>
    public string CaptureDir { get; }
    /// <summary>설정 파일을 읽을 때 문제가 있었으면(깨진 파일 등) 그 사유. 이 경우 기본값으로 시작한다. 없으면 null.</summary>
    public string? SettingsWarning { get; }

    /// <summary>파일에 저장된 설정.</summary>
    public AppSettings Persisted { get; private set; }
    /// <summary>실제로 쓰는 설정 = 저장된 설정 + 실행 인자(--folder --model --threshold) 덮어쓰기. 인자는 저장되지 않는다.</summary>
    public AppSettings Effective { get; private set; }

    /// <summary>현재 로그인한 세션. 로그인 전에는 null.</summary>
    public Session? Session { get; set; }
    /// <summary>true면 메인 창이 닫힌 뒤 앱을 끝내지 않고 로그인 창으로 돌아간다(수동 로그아웃·자동 로그아웃).</summary>
    public bool LogoutRequested { get; set; }
    /// <summary>DB를 복원한 뒤 서비스(연결·설정)를 새로 만들어 다시 시작하라는 요청.</summary>
    public bool RestartRequested { get; set; }

    // 자동 백업이 동시에 두 번 실행되지 않게 하는 플래그(0=안 돌고 있음, 1=실행 중). Interlocked로 원자적으로 바꾼다.
    private int _autoBackupRunning;

    /// <summary>
    /// 자동 백업 주기가 지났으면 백업을 만든다(앱이 켜져 있고 로그인돼 있을 때만 호출됨). 동시에 두 번 돌지 않는다.
    /// 성공·실패 모두 감사 로그에 "자동"으로 남기며 실패해도 예외를 던지지 않는다. 만들었으면 결과를, 아니면 null.
    /// </summary>
    public async Task<VisionQC.Data.BackupResult?> RunAutoBackupIfDueAsync(Session session)
    {
        // 설정의 자동 백업 주기(시간). 0 이하는 "끔". IsDue는 백업 폴더의 가장 최근 백업 시각을 보고 주기가 지났는지 판단한다.
        int hours = Effective.AutoBackupHours;
        if (hours <= 0 || !VisionQC.Data.SqliteBackup.IsDue(BackupDir, TimeSpan.FromHours(hours), DateTime.Now)) return null;
        // 이미 다른 곳에서 백업 중이면(이전 값이 1) 이번 호출은 건너뛴다. 끝나면 finally에서 0으로 되돌린다.
        if (Interlocked.Exchange(ref _autoBackupRunning, 1) == 1) return null;
        try
        {
            var r = await VisionQC.Data.SqliteBackup.CreateAsync(DbPath, BackupDir);
            await session.AuditAsync(AuditActions.DbBackup, $"자동 · {Path.GetFileName(r.Path)} ({r.Bytes / 1024:N0} KB)" + (r.Pruned > 0 ? $", 오래된 백업 {r.Pruned}개 정리" : ""));
            return r;
        }
        catch (Exception ex)
        {
            await session.AuditAsync(AuditActions.DbBackup, $"자동 백업 실패: {ex.Message}");
            return null;
        }
        finally { Interlocked.Exchange(ref _autoBackupRunning, 0); }
    }
    /// <summary>다시 뜬 로그인 창에 보여 줄 안내(자동 로그아웃 사유 등). 한 번 쓰면 비운다.</summary>
    public string? LogoutNotice { get; set; }

    /// <summary>설정이 저장·적용되면 발생한다. 검사 화면의 설정 요약 갱신, 자동 로그아웃 시간 재설정 등이 구독한다.</summary>
    public event Action? SettingsChanged;

    /// <summary>
    /// 서비스들을 만든다. 경로는 "실행 인자 &gt; 기본 위치(%LOCALAPPDATA%\VisionQC)" 순으로 정한다.
    /// DB 파일을 열 수 없으면 여기서 예외가 나며, App.OnStartup이 메시지를 띄우고 종료한다.
    /// </summary>
    public AppServices(IReadOnlyDictionary<string, string> args)
    {
        Args = args;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        DbPath = args.GetValueOrDefault("db") ?? Path.Combine(local, "VisionQC", "visionqc.db");
        BackupDir = args.GetValueOrDefault("backup-dir") ?? Path.Combine(local, "VisionQC", "backup");
        CaptureDir = args.GetValueOrDefault("capture-dir") ?? Path.Combine(local, "VisionQC", "captures");
        Results = new SqliteResultRepository(DbPath);
        Accounts = new SqliteAccountRepository(DbPath);
        Audit = new AuditLogger(Accounts);
        Auth = new AuthService(Accounts, Audit);

        SettingsStore = new SettingsStore(args.GetValueOrDefault("settings") ?? Path.Combine(local, "VisionQC", "settings.json"));
        var (loaded, warning) = SettingsStore.Load();
        SettingsWarning = warning;
        Persisted = loaded;

        // 실행 인자는 파일에 저장하지 않고 "이번 실행의 Effective 값"에만 덮어쓴다(시연·자동화용).
        // 임계값 인자는 문화권 영향을 받지 않도록 InvariantCulture로 파싱하고, 0~1 밖의 값은 Clamp로 잘라 낸다.
        var eff = loaded;
        if (args.TryGetValue("folder", out var folder)) eff = eff with { FolderPath = folder };
        if (args.TryGetValue("model", out var model)) eff = eff with { ModelPath = model };
        if (args.TryGetValue("threshold", out var th) &&
            double.TryParse(th, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && double.IsFinite(v))
            eff = eff with { Threshold = Math.Clamp(v, 0, 1) };
        Effective = eff;
    }

    /// <summary>엔지니어 이상이 설정 탭에서 저장. 파일에 쓰고, 실행 인자 덮어쓰기는 이 값으로 대체된다.</summary>
    public void ApplySettings(AppSettings s)
    {
        SettingsStore.Save(s);              // 검증 실패 시 예외 → 상태는 바뀌지 않는다
        Persisted = s;
        Effective = s;
        SettingsChanged?.Invoke();
    }

    /// <summary>마지막으로 쓴 폴더만 저장(누구나). 인자로 덮어쓴 모델·임계값이 파일에 새어 들어가지 않도록 Persisted를 기준으로 한다.</summary>
    public void RememberFolder(string folder)
    {
        if (Persisted.FolderPath == folder && Effective.FolderPath == folder) return;
        try
        {
            var next = Persisted with { FolderPath = folder };
            SettingsStore.Save(next);
            Persisted = next;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { /* 저장 실패는 검사를 막지 않는다 */ }
        Effective = Effective with { FolderPath = folder };
    }
}

/// <summary>로그인한 사용자와 권한. 감사 기록 도우미를 함께 든다.</summary>
/// <summary>
/// 로그인한 사용자와 권한. 화면(ViewModel)은 Can(...)으로 "이 동작을 해도 되는가"를 묻고, AuditAsync로 감사 로그를 남긴다.
/// 화면에서 버튼을 숨기는 것은 편의일 뿐이고, 실제 보안은 서비스 계층에서 다시 검사하는 것으로 지킨다.
/// </summary>
public sealed class Session
{
    private readonly AppServices _svc;
    /// <summary>로그인한 계정(아이디·역할 등).</summary>
    public UserAccount User { get; }
    /// <summary>로그인 성공 후 App.xaml.cs에서 만든다.</summary>
    public Session(UserAccount user, AppServices svc) { User = user; _svc = svc; }

    /// <summary>이 사용자의 역할이 해당 권한(Permission)을 갖는지. 예: 정정=ReviewResults, 설정=ChangeSettings, 사용자 관리=ManageUsers.</summary>
    public bool Can(Permission p) => Permissions.Has(User.Role, p);
    /// <summary>이 사용자 이름으로 감사 로그 한 줄을 남긴다. 기록에 성공하면 true(실패해도 예외는 던지지 않고 false).</summary>
    public Task<bool> AuditAsync(string action, string? detail = null) => _svc.Audit.WriteAsync(User, User.LoginId, action, detail);
    /// <summary>"아이디 (역할)" 형태의 문자열(로그·디버깅용).</summary>
    public override string ToString() => $"{User.LoginId} ({User.Role})";
}
