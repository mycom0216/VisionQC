using System.Globalization;
using Microsoft.Data.Sqlite;

namespace VisionQC.Data;

public sealed record BackupResult(string Path, long Bytes, int Pruned);

/// <summary>
/// 이력 DB 백업. SQLite의 VACUUM INTO로 "한 시점의 일관된 사본"을 만든다 — 앱이 쓰는 중이어도(WAL 모드) 파일을 그냥 복사할 때처럼
/// 반쯤 쓰인 상태가 섞이지 않는다. 만든 뒤 사본을 열어 무결성 검사(PRAGMA integrity_check)를 통과해야 성공으로 친다.
/// 보관: 같은 폴더의 `visionqc_YYYYMMDD_HHMMSS*.db` 중 최신 N개만 남기고 지운다(다른 파일은 건드리지 않는다).
/// 주의: 백업에는 계정 정보(비밀번호 해시)와 감사 로그가 들어 있으므로 폴더 접근 권한을 DB와 같게 관리해야 한다.
/// </summary>
public static class SqliteBackup
{
    public const string Prefix = "visionqc_";
    public const int DefaultKeep = 10;

    public static async Task<BackupResult> CreateAsync(string dbPath, string backupDir, int keep = DefaultKeep,
        Func<DateTime>? now = null, CancellationToken ct = default)
    {
        if (!File.Exists(dbPath)) throw new FileNotFoundException($"백업할 DB 파일이 없음: {dbPath}", dbPath);
        if (keep < 1) throw new ArgumentOutOfRangeException(nameof(keep), "보관 개수는 1 이상이어야 합니다.");
        Directory.CreateDirectory(backupDir);

        var stamp = (now?.Invoke() ?? DateTime.Now).ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var target = System.IO.Path.Combine(backupDir, $"{Prefix}{stamp}.db");
        for (int i = 2; File.Exists(target); i++)                       // 같은 초에 또 만들면 번호를 붙인다(덮어쓰지 않음)
            target = System.IO.Path.Combine(backupDir, $"{Prefix}{stamp}_{i}.db");

        try
        {
            var src = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
            await using (var c = new SqliteConnection(src))
            {
                await c.OpenAsync(ct).ConfigureAwait(false);
                await using var cmd = c.CreateCommand();
                cmd.CommandText = "VACUUM INTO $f";
                cmd.Parameters.AddWithValue("$f", target);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await VerifyAsync(target, ct).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(target);                                           // 실패한 반쪽 사본을 남기지 않는다
            throw;
        }

        int pruned = Prune(backupDir, keep);
        return new BackupResult(target, new FileInfo(target).Length, pruned);
    }

    /// <summary>사본을 열어 구조·내용 무결성을 확인한다. 문제가 있으면 InvalidDataException.</summary>
    public static async Task VerifyAsync(string path, CancellationToken ct = default)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        await using var c = new SqliteConnection(cs);
        await c.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check";
        var result = Convert.ToString(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"백업 무결성 검사 실패: {result}");
    }

    /// <summary>`visionqc_*.db` 중 이름순(= 시각순)으로 가장 오래된 것부터 지워 최신 keep개만 남긴다. 지운 개수를 반환.</summary>
    public static int Prune(string backupDir, int keep)
    {
        if (!Directory.Exists(backupDir)) return 0;
        var files = Directory.EnumerateFiles(backupDir, Prefix + "*.db")
            .Where(f => System.IO.Path.GetFileName(f).Length >= Prefix.Length + 15)     // visionqc_YYYYMMDD_HHMMSS...
            .OrderBy(f => System.IO.Path.GetFileName(f), StringComparer.Ordinal)
            .ToList();
        int pruned = 0;
        foreach (var f in files.Take(Math.Max(0, files.Count - keep)))
            if (TryDelete(f)) pruned++;
        return pruned;
    }

    private static bool TryDelete(string path)
    {
        try { File.Delete(path); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
