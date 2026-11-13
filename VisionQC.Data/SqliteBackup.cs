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
        // 이름 형식(visionqc_YYYYMMDD_HHMMSS[_n].db)이 정확히 맞는 파일만 대상으로 한다 — 사람이 둔 다른 파일은 건드리지 않는다
        var files = List(backupDir).OrderBy(b => b.Name, StringComparer.Ordinal).ToList();
        int pruned = 0;
        foreach (var f in files.Take(Math.Max(0, files.Count - keep)))
            if (TryDelete(f.Path)) pruned++;
        return pruned;
    }

    public sealed record BackupInfo(string Path, string Name, long Bytes, DateTime StampLocal);

    /// <summary>`visionqc_YYYYMMDD_HHMMSS[_번호].db` 형식의 파일 이름에서 만든 시각(로컬). 형식이 다르면 false.</summary>
    public static bool TryParseStamp(string fileName, out DateTime stampLocal)
    {
        stampLocal = default;
        var m = System.Text.RegularExpressions.Regex.Match(fileName, @"^visionqc_(\d{8}_\d{6})(?:_\d+)?\.db$");
        return m.Success && DateTime.TryParseExact(m.Groups[1].Value, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out stampLocal);
    }

    /// <summary>백업 폴더의 백업 목록(최신순). 폴더가 없으면 빈 목록.</summary>
    public static IReadOnlyList<BackupInfo> List(string backupDir)
    {
        if (!Directory.Exists(backupDir)) return Array.Empty<BackupInfo>();
        var list = new List<BackupInfo>();
        foreach (var f in Directory.EnumerateFiles(backupDir, Prefix + "*.db"))
        {
            var name = System.IO.Path.GetFileName(f);
            if (TryParseStamp(name, out var stamp)) list.Add(new BackupInfo(f, name, new FileInfo(f).Length, stamp));
        }
        return list.OrderByDescending(b => b.StampLocal).ThenByDescending(b => b.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>가장 최근 백업이 interval보다 오래됐거나 백업이 하나도 없으면 true(자동 백업 시점).</summary>
    public static bool IsDue(string backupDir, TimeSpan interval, DateTime nowLocal)
    {
        var newest = List(backupDir).FirstOrDefault();
        return newest is null || nowLocal - newest.StampLocal >= interval;
    }

    public sealed record RestoreResult(string SafetyCopyPath);

    /// <summary>
    /// 백업으로 DB를 되돌린다. 순서: ① 백업 무결성 확인 ② **현재 DB를 먼저 안전 사본으로 저장**(prerestore_visionqc_*.db — 복원이 잘못됐을 때 되돌릴 수 있게)
    /// ③ 임시 파일로 복사 ④ -wal/-shm 삭제 후 교체. ①~③에서 실패하면 현재 DB는 그대로다.
    /// 호출하는 쪽은 이 DB를 쓰는 작업(검사 등)이 없도록 먼저 막아야 하고, 복원 뒤에는 앱을 다시 시작해야 한다.
    /// </summary>
    public static async Task<RestoreResult> RestoreAsync(string backupPath, string dbPath, string safetyDir,
        Func<DateTime>? now = null, CancellationToken ct = default)
    {
        if (!File.Exists(backupPath)) throw new FileNotFoundException($"복원할 백업이 없음: {backupPath}", backupPath);
        if (string.Equals(System.IO.Path.GetFullPath(backupPath), System.IO.Path.GetFullPath(dbPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("백업 파일과 현재 DB가 같은 파일입니다.");
        await VerifyAsync(backupPath, ct).ConfigureAwait(false);          // ① 깨진 백업으로 덮어쓰지 않는다

        SqliteConnection.ClearAllPools();                                 // 앱이 쥐고 있던 연결 풀을 놓는다
        Directory.CreateDirectory(safetyDir);
        var stamp = (now?.Invoke() ?? DateTime.Now).ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var safety = System.IO.Path.Combine(safetyDir, $"prerestore_visionqc_{stamp}.db");
        for (int i = 2; File.Exists(safety); i++) safety = System.IO.Path.Combine(safetyDir, $"prerestore_visionqc_{stamp}_{i}.db");

        if (File.Exists(dbPath))                                          // ② 현재 DB 안전 사본(일관된 스냅샷)
        {
            var src = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
            await using var c = new SqliteConnection(src);
            await c.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "VACUUM INTO $f";
            cmd.Parameters.AddWithValue("$f", safety);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var tmp = dbPath + ".restore.tmp";
        try
        {
            File.Copy(backupPath, tmp, overwrite: true);                  // ③
            TryDelete(dbPath + "-wal"); TryDelete(dbPath + "-shm");       // ④ 이전 WAL이 섞이지 않게
            File.Move(tmp, dbPath, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
        SqliteConnection.ClearAllPools();
        return new RestoreResult(safety);
    }

    private static bool TryDelete(string path)
    {
        try { File.Delete(path); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
