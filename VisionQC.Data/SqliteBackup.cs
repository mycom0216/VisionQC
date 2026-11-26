// ============================================================================
// SqliteBackup.cs
// 역할: 이력 DB(검사 이력 + 계정 + 감사 로그)의 백업 / 무결성 검증 / 보관 개수 정리 / 복원.
// 파이프라인에서의 위치: 검사 흐름(이미지 -> 전처리 -> 추론 -> 판정 -> 저장소)의 "저장소 보호" 부분이다.
//   UI가 주기적으로(IsDue) 또는 사용자가 누를 때 CreateAsync를, 문제가 생기면 RestoreAsync를 호출한다.
// 설계 원칙: 실패해도 원본 DB를 망가뜨리지 않는다(백업은 읽기 전용으로 열고, 복원은 안전 사본을 먼저 만든다).
// ============================================================================
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace VisionQC.Data;

/// <summary>백업 결과: 만들어진 파일 경로, 크기(바이트), 보관 개수 초과로 지운 오래된 백업 수.</summary>
public sealed record BackupResult(string Path, long Bytes, int Pruned);

/// <summary>
/// 이력 DB 백업. SQLite의 VACUUM INTO로 "한 시점의 일관된 사본"을 만든다 — 앱이 쓰는 중이어도(WAL 모드) 파일을 그냥 복사할 때처럼
/// 반쯤 쓰인 상태가 섞이지 않는다. 만든 뒤 사본을 열어 무결성 검사(PRAGMA integrity_check)를 통과해야 성공으로 친다.
/// 보관: 같은 폴더의 `visionqc_YYYYMMDD_HHMMSS*.db` 중 최신 N개만 남기고 지운다(다른 파일은 건드리지 않는다).
/// 주의: 백업에는 계정 정보(비밀번호 해시)와 감사 로그가 들어 있으므로 폴더 접근 권한을 DB와 같게 관리해야 한다.
/// </summary>
public static class SqliteBackup
{
    /// <summary>백업 파일 이름 접두사. 이 접두사와 시각 형식을 모두 만족하는 파일만 "우리가 만든 백업"으로 인정한다.</summary>
    public const string Prefix = "visionqc_";
    /// <summary>기본 보관 개수(최신 10개). 디스크가 무한히 차는 것을 막는다.</summary>
    public const int DefaultKeep = 10;

    /// <summary>
    /// 백업을 하나 만든다: VACUUM INTO로 사본 생성 → 무결성 검사 → 오래된 백업 정리.
    /// </summary>
    /// <param name="dbPath">원본 DB 파일</param>
    /// <param name="backupDir">백업을 둘 폴더(없으면 만든다)</param>
    /// <param name="keep">남길 최신 백업 개수(1 이상)</param>
    /// <param name="now">현재 시각 공급자(테스트에서 시간을 고정하려는 용도; 기본은 DateTime.Now)</param>
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
            // 원본은 읽기 전용(ReadOnly)으로 연다 — 백업 도중 실수로 원본을 바꾸지 않는다. 풀링을 꺼서 연결이 끝나면 파일 핸들도 바로 놓는다.
            var src = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
            await using (var c = new SqliteConnection(src))
            {
                await c.OpenAsync(ct).ConfigureAwait(false);
                await using var cmd = c.CreateCommand();
                // VACUUM INTO: 현재 DB의 일관된 스냅샷을 새 파일로 기록한다(빈 공간도 정리되어 파일이 작아진다). 파일 경로도 바인딩한다.
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

        // 성공한 뒤에만 오래된 백업을 지운다(새 백업이 실패했는데 옛 백업만 사라지는 일이 없게 이 순서를 지킨다).
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
        // integrity_check: SQLite가 파일의 페이지·인덱스 구조를 전수 검사한다. 정상이면 한 줄 "ok"만 돌려준다.
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

    /// <summary>백업 목록의 한 항목: 전체 경로, 파일 이름, 크기, 이름에서 읽은 생성 시각(로컬).</summary>
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

    /// <summary>복원 결과: 복원 직전의 현재 DB를 보관해 둔 안전 사본 경로(잘못 복원했을 때 되돌리는 용도).</summary>
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

        // 임시 파일에 먼저 복사한 뒤 이름을 바꿔 교체한다 — 복사 도중 실패해도 dbPath가 반쯤 덮어쓰인 상태가 되지 않는다.
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

    /// <summary>파일 삭제를 시도하고 성공 여부를 돌려준다. 다른 프로그램이 쓰는 중이거나 권한이 없어도 예외를 던지지 않는다(정리 작업이 본 작업을 망치지 않게).</summary>
    private static bool TryDelete(string path)
    {
        try { File.Delete(path); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
