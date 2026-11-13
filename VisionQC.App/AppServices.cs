using System.IO;
using VisionQC.Core;
using VisionQC.Data;

namespace VisionQC.App;

/// <summary>앱 전체가 공유하는 서비스 묶음. 실행 인자와 설정 파일을 합쳐 "지금 쓰는 설정(Effective)"을 관리한다.</summary>
public sealed class AppServices
{
    public IReadOnlyDictionary<string, string> Args { get; }
    public SqliteResultRepository Results { get; }
    public SqliteAccountRepository Accounts { get; }
    public AuditLogger Audit { get; }
    public AuthService Auth { get; }
    public SettingsStore SettingsStore { get; }
    public string DbPath { get; }
    /// <summary>DB 백업 폴더(기본 %LOCALAPPDATA%\VisionQC\backup, 실행 인자 --backup-dir).</summary>
    public string BackupDir { get; }
    public string? SettingsWarning { get; }

    /// <summary>파일에 저장된 설정.</summary>
    public AppSettings Persisted { get; private set; }
    /// <summary>실제로 쓰는 설정 = 저장된 설정 + 실행 인자(--folder --model --threshold) 덮어쓰기. 인자는 저장되지 않는다.</summary>
    public AppSettings Effective { get; private set; }

    public Session? Session { get; set; }
    public bool LogoutRequested { get; set; }
    /// <summary>다시 뜬 로그인 창에 보여 줄 안내(자동 로그아웃 사유 등). 한 번 쓰면 비운다.</summary>
    public string? LogoutNotice { get; set; }

    public event Action? SettingsChanged;

    public AppServices(IReadOnlyDictionary<string, string> args)
    {
        Args = args;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        DbPath = args.GetValueOrDefault("db") ?? Path.Combine(local, "VisionQC", "visionqc.db");
        BackupDir = args.GetValueOrDefault("backup-dir") ?? Path.Combine(local, "VisionQC", "backup");
        Results = new SqliteResultRepository(DbPath);
        Accounts = new SqliteAccountRepository(DbPath);
        Audit = new AuditLogger(Accounts);
        Auth = new AuthService(Accounts, Audit);

        SettingsStore = new SettingsStore(args.GetValueOrDefault("settings") ?? Path.Combine(local, "VisionQC", "settings.json"));
        var (loaded, warning) = SettingsStore.Load();
        SettingsWarning = warning;
        Persisted = loaded;

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
public sealed class Session
{
    private readonly AppServices _svc;
    public UserAccount User { get; }
    public Session(UserAccount user, AppServices svc) { User = user; _svc = svc; }

    public bool Can(Permission p) => Permissions.Has(User.Role, p);
    public Task<bool> AuditAsync(string action, string? detail = null) => _svc.Audit.WriteAsync(User, User.LoginId, action, detail);
    public override string ToString() => $"{User.LoginId} ({User.Role})";
}
