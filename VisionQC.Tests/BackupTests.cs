using VisionQC.Core;
using VisionQC.Data;

namespace VisionQC.Tests;

public class BackupTests
{
    private static async Task<(string db, SqliteResultRepository repo, SqliteAccountRepository acc)> MakeDb(string dir, int results = 3)
    {
        var db = System.IO.Path.Combine(dir, "visionqc.db");
        var repo = new SqliteResultRepository(db);
        var acc = new SqliteAccountRepository(db);
        var job = await repo.BeginJobAsync(new ModelInfo("m", "v", "c"), "s", 0.5f);
        for (int i = 0; i < results; i++)
            await repo.SaveAsync(new InspectionResult { JobId = job, ImagePath = $"{i}.png", Verdict = Verdict.OK, Score = 0.1f, InspectedAtUtc = DateTime.UtcNow });
        await acc.CreateFirstAdminAsync("admin", "hash");
        return (db, repo, acc);
    }

    [Fact]
    public async Task Backup_is_a_consistent_copy_that_opens_with_the_same_data()
    {
        using var d = new TempDir();
        var (db, repo, _) = await MakeDb(d.Path, results: 5);
        var res = await SqliteBackup.CreateAsync(db, System.IO.Path.Combine(d.Path, "bk"));

        Assert.True(File.Exists(res.Path));
        Assert.True(res.Bytes > 0);
        Assert.StartsWith("visionqc_", System.IO.Path.GetFileName(res.Path));

        var copy = new SqliteResultRepository(res.Path);                 // 사본을 일반 DB처럼 열 수 있어야 한다
        Assert.Equal(5, (await copy.QueryAsync(new HistoryFilter())).Count);
        var acc = new SqliteAccountRepository(res.Path);
        Assert.NotNull(await acc.FindByLoginIdAsync("admin"));            // 계정도 포함(→ 백업 폴더 접근 권한 주의)
    }

    [Fact]
    public async Task Backup_does_not_change_or_lock_the_original_and_works_while_it_is_in_use()
    {
        using var d = new TempDir();
        var (db, repo, _) = await MakeDb(d.Path, results: 2);
        var before = (await repo.QueryAsync(new HistoryFilter())).Count;

        var backup = SqliteBackup.CreateAsync(db, System.IO.Path.Combine(d.Path, "bk"));
        var job = await repo.BeginJobAsync(new ModelInfo("m", "v", "c"), "s", 0.5f);       // 백업 중에도 쓰기 가능
        await repo.SaveAsync(new InspectionResult { JobId = job, ImagePath = "x.png", Verdict = Verdict.NG, Score = 0.9f });
        var res = await backup;

        Assert.Equal(before + 1, (await repo.QueryAsync(new HistoryFilter())).Count);
        var snapshot = (await new SqliteResultRepository(res.Path).QueryAsync(new HistoryFilter())).Count;
        Assert.InRange(snapshot, before, before + 1);                    // 한 시점의 상태(중간 상태 없음)
    }

    [Fact]
    public async Task Same_second_does_not_overwrite_an_earlier_backup()
    {
        using var d = new TempDir();
        var (db, _, _) = await MakeDb(d.Path);
        var fixedNow = new DateTime(2026, 11, 13, 9, 0, 0);
        var bk = System.IO.Path.Combine(d.Path, "bk");
        var a = await SqliteBackup.CreateAsync(db, bk, now: () => fixedNow);
        var b = await SqliteBackup.CreateAsync(db, bk, now: () => fixedNow);
        Assert.NotEqual(a.Path, b.Path);
        Assert.True(File.Exists(a.Path) && File.Exists(b.Path));
    }

    [Fact]
    public async Task Prune_keeps_only_the_newest_and_never_touches_other_files()
    {
        using var d = new TempDir();
        var (db, _, _) = await MakeDb(d.Path);
        var bk = System.IO.Path.Combine(d.Path, "bk");
        Directory.CreateDirectory(bk);
        File.WriteAllText(System.IO.Path.Combine(bk, "notes.txt"), "keep me");
        File.WriteAllText(System.IO.Path.Combine(bk, "visionqc_old.db"), "not a timestamped backup name");   // 형식이 다르면 건드리지 않음

        for (int i = 0; i < 5; i++)
            await SqliteBackup.CreateAsync(db, bk, keep: 3, now: () => new DateTime(2026, 11, 13, 9, 0, i));

        var names = Directory.GetFiles(bk).Select(System.IO.Path.GetFileName).OrderBy(x => x).ToArray();
        Assert.Contains("notes.txt", names);
        Assert.Contains("visionqc_old.db", names);
        var backups = names.Where(n => n!.StartsWith("visionqc_2026")).ToArray();
        Assert.Equal(new[] { "visionqc_20261113_090002.db", "visionqc_20261113_090003.db", "visionqc_20261113_090004.db" }, backups);
    }

    [Fact]
    public async Task Missing_source_throws_and_leaves_nothing_behind()
    {
        using var d = new TempDir();
        var bk = System.IO.Path.Combine(d.Path, "bk");
        await Assert.ThrowsAsync<FileNotFoundException>(() => SqliteBackup.CreateAsync(System.IO.Path.Combine(d.Path, "none.db"), bk));
        Assert.False(Directory.Exists(bk) && Directory.GetFiles(bk).Length > 0);
    }

    [Fact]
    public async Task Keep_must_be_positive()
    {
        using var d = new TempDir();
        var (db, _, _) = await MakeDb(d.Path);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => SqliteBackup.CreateAsync(db, System.IO.Path.Combine(d.Path, "bk"), keep: 0));
    }

    [Fact]
    public async Task Verify_rejects_a_corrupt_file()
    {
        using var d = new TempDir();
        var bad = System.IO.Path.Combine(d.Path, "bad.db");
        File.WriteAllBytes(bad, Enumerable.Range(0, 4096).Select(i => (byte)(i * 31)).ToArray());
        await Assert.ThrowsAnyAsync<Exception>(() => SqliteBackup.VerifyAsync(bad));      // 열 수 없거나 integrity_check 실패
    }
}
