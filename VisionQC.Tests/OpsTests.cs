using Microsoft.Data.Sqlite;
using VisionQC.Core;
using VisionQC.Data;

namespace VisionQC.Tests;

public class BackupListAndRestoreTests
{
    private static async Task<(string db, SqliteResultRepository repo, SqliteAccountRepository acc)> MakeDb(string dir, int results)
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

    [Theory]
    [InlineData("visionqc_20261113_090000.db", true)]
    [InlineData("visionqc_20261113_090000_2.db", true)]
    [InlineData("visionqc_old.db", false)]
    [InlineData("visionqc_20261313_090000.db", false)]       // 존재하지 않는 날짜
    [InlineData("prerestore_visionqc_20261113_090000.db", false)]
    [InlineData("visionqc_20261113_090000.db.bak", false)]
    public void Stamp_parsing(string name, bool ok) => Assert.Equal(ok, SqliteBackup.TryParseStamp(name, out _));

    [Fact]
    public async Task List_is_newest_first_and_ignores_foreign_files()
    {
        using var d = new TempDir();
        var (db, _, _) = await MakeDb(d.Path, 1);
        var bk = System.IO.Path.Combine(d.Path, "bk");
        await SqliteBackup.CreateAsync(db, bk, now: () => new DateTime(2026, 11, 13, 9, 0, 1));
        await SqliteBackup.CreateAsync(db, bk, now: () => new DateTime(2026, 11, 13, 9, 0, 3));
        await SqliteBackup.CreateAsync(db, bk, now: () => new DateTime(2026, 11, 13, 9, 0, 2));
        File.WriteAllText(System.IO.Path.Combine(bk, "visionqc_notes.db"), "x");
        File.WriteAllText(System.IO.Path.Combine(bk, "readme.txt"), "x");

        var list = SqliteBackup.List(bk);
        Assert.Equal(new[] { "visionqc_20261113_090003.db", "visionqc_20261113_090002.db", "visionqc_20261113_090001.db" },
            list.Select(b => b.Name).ToArray());
        Assert.All(list, b => Assert.True(b.Bytes > 0));
        Assert.Empty(SqliteBackup.List(System.IO.Path.Combine(d.Path, "none")));
    }

    [Fact]
    public async Task IsDue_when_no_backup_or_newest_is_older_than_interval()
    {
        using var d = new TempDir();
        var (db, _, _) = await MakeDb(d.Path, 1);
        var bk = System.IO.Path.Combine(d.Path, "bk");
        var now = new DateTime(2026, 11, 13, 12, 0, 0);
        Assert.True(SqliteBackup.IsDue(bk, TimeSpan.FromHours(24), now));                 // 폴더 자체가 없음 = 백업 없음

        await SqliteBackup.CreateAsync(db, bk, now: () => now.AddHours(-23));
        Assert.False(SqliteBackup.IsDue(bk, TimeSpan.FromHours(24), now));                // 23시간 전 → 아직
        Assert.True(SqliteBackup.IsDue(bk, TimeSpan.FromHours(24), now.AddHours(1)));     // 24시간 경과(경계 포함)
        Assert.False(SqliteBackup.IsDue(bk, TimeSpan.FromHours(48), now.AddHours(1)));
    }

    [Fact]
    public async Task Restore_replaces_db_with_backup_and_keeps_a_safety_copy_of_the_current_one()
    {
        using var d = new TempDir();
        var (db, repo, _) = await MakeDb(d.Path, 3);
        var bk = System.IO.Path.Combine(d.Path, "bk");
        var backup = await SqliteBackup.CreateAsync(db, bk);                              // 3건일 때의 백업

        var job = await repo.BeginJobAsync(new ModelInfo("m", "v", "c"), "s", 0.5f);
        for (int i = 0; i < 4; i++)                                                       // 그 뒤 4건 더 → 현재 7건
            await repo.SaveAsync(new InspectionResult { JobId = job, ImagePath = $"late{i}.png", Verdict = Verdict.NG, Score = 0.9f });
        Assert.Equal(7, (await repo.QueryAsync(new HistoryFilter())).Count);

        var res = await SqliteBackup.RestoreAsync(backup.Path, db, System.IO.Path.Combine(d.Path, "safety"));

        Assert.Equal(3, (await new SqliteResultRepository(db).QueryAsync(new HistoryFilter())).Count);        // 백업 시점으로 복원
        Assert.NotNull(await new SqliteAccountRepository(db).FindByLoginIdAsync("admin"));                   // 계정도 복원
        Assert.True(File.Exists(res.SafetyCopyPath));                                                         // 복원 직전 상태 보존
        Assert.StartsWith("prerestore_visionqc_", System.IO.Path.GetFileName(res.SafetyCopyPath));
        Assert.Equal(7, (await new SqliteResultRepository(res.SafetyCopyPath).QueryAsync(new HistoryFilter())).Count);
        Assert.False(File.Exists(db + ".restore.tmp"));
        Assert.Empty(SqliteBackup.List(System.IO.Path.Combine(d.Path, "safety")));                            // 안전 사본은 일반 백업 목록·정리 대상이 아님
    }

    [Fact]
    public async Task Restore_refuses_a_corrupt_backup_and_leaves_the_current_db_untouched()
    {
        using var d = new TempDir();
        var (db, repo, _) = await MakeDb(d.Path, 3);
        var bad = System.IO.Path.Combine(d.Path, "visionqc_20261113_090000.db");
        File.WriteAllBytes(bad, Enumerable.Range(0, 4096).Select(i => (byte)(i * 31)).ToArray());

        await Assert.ThrowsAnyAsync<Exception>(() => SqliteBackup.RestoreAsync(bad, db, System.IO.Path.Combine(d.Path, "safety")));
        Assert.Equal(3, (await new SqliteResultRepository(db).QueryAsync(new HistoryFilter())).Count);
        Assert.False(Directory.Exists(System.IO.Path.Combine(d.Path, "safety")) && Directory.GetFiles(System.IO.Path.Combine(d.Path, "safety")).Length > 0);
    }

    [Fact]
    public async Task Restore_rejects_missing_backup_and_same_file()
    {
        using var d = new TempDir();
        var (db, _, _) = await MakeDb(d.Path, 1);
        await Assert.ThrowsAsync<FileNotFoundException>(() => SqliteBackup.RestoreAsync(System.IO.Path.Combine(d.Path, "none.db"), db, d.Path));
        await Assert.ThrowsAsync<ArgumentException>(() => SqliteBackup.RestoreAsync(db, db, d.Path));
    }

    [Fact]
    public async Task Prune_ignores_files_whose_names_only_look_similar()
    {
        using var d = new TempDir();
        var (db, _, _) = await MakeDb(d.Path, 1);
        var bk = System.IO.Path.Combine(d.Path, "bk");
        Directory.CreateDirectory(bk);
        File.WriteAllText(System.IO.Path.Combine(bk, "visionqc_my_manual_copy_before_upgrade.db"), "mine");   // 길지만 형식이 다른 사용자 파일
        for (int i = 0; i < 3; i++) await SqliteBackup.CreateAsync(db, bk, keep: 1, now: () => new DateTime(2026, 11, 13, 9, 0, i));
        Assert.True(File.Exists(System.IO.Path.Combine(bk, "visionqc_my_manual_copy_before_upgrade.db")));
        Assert.Single(SqliteBackup.List(bk));
    }
}

public class PurgeTests
{
    private static async Task<(SqliteResultRepository repo, long jobOld, long jobNew)> Seed(string dir)
    {
        var repo = new SqliteResultRepository(System.IO.Path.Combine(dir, "t.db"));
        var now = DateTime.UtcNow;
        var jobOld = await repo.BeginJobAsync(new ModelInfo("m", "v1", "c"), "old", 0.5f);
        var jobNew = await repo.BeginJobAsync(new ModelInfo("m", "v2", "c"), "new", 0.5f);
        await repo.EndJobAsync(jobOld, JobStatus.Completed);
        await repo.EndJobAsync(jobNew, JobStatus.Completed);
        for (int i = 0; i < 3; i++)
            await repo.SaveAsync(new InspectionResult
            {
                JobId = jobOld, ImagePath = $"old{i}.png", Verdict = Verdict.NG, Score = 0.9f, InspectedAtUtc = now.AddDays(-100),
                Defects = new[] { new Detection("scratch", 0.9f, 1, 1, 2, 2) }
            });
        for (int i = 0; i < 2; i++)
            await repo.SaveAsync(new InspectionResult { JobId = jobNew, ImagePath = $"new{i}.png", Verdict = Verdict.OK, Score = 0.1f, InspectedAtUtc = now.AddDays(-5) });
        return (repo, jobOld, jobNew);
    }

    [Fact]
    public async Task Count_then_purge_deletes_only_older_results_with_their_defects_and_reviews()
    {
        using var d = new TempDir();
        var (repo, _, _) = await Seed(d.Path);
        var oldId = (await repo.QueryAsync(new HistoryFilter())).First(r => r.ImagePath == "old0.png").Id;
        await repo.AddReviewAsync(new Review(0, oldId, "kim", Verdict.OK, "x", DateTime.UtcNow));

        var cutoff = DateTime.UtcNow.AddDays(-60);
        Assert.Equal(3, await repo.CountOlderThanAsync(cutoff));

        var res = await repo.PurgeOlderThanAsync(cutoff);
        Assert.Equal((3, 1), (res.Results, res.Jobs));                       // 결과 3건 + 비어 버린 옛 회차 1개

        var left = await repo.QueryAsync(new HistoryFilter());
        Assert.Equal(new[] { "new0.png", "new1.png" }, left.Select(r => r.ImagePath).OrderBy(x => x).ToArray());
        Assert.Empty(await repo.GetReviewsAsync(oldId));                      // 정정도 함께 삭제(CASCADE)
        Assert.Empty((await repo.QueryStatsAsync(new StatsFilter())).Defects);   // 불량 목록도 삭제(CASCADE)
        Assert.Equal(0, await repo.CountOlderThanAsync(cutoff));
    }

    [Fact]
    public async Task Purge_refuses_cutoff_inside_minimum_retention()
    {
        using var d = new TempDir();
        var (repo, _, _) = await Seed(d.Path);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repo.PurgeOlderThanAsync(DateTime.UtcNow.AddDays(-29)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repo.PurgeOlderThanAsync(DateTime.UtcNow));
        Assert.Equal(5, (await repo.QueryAsync(new HistoryFilter())).Count);   // 아무것도 지워지지 않음
        await repo.PurgeOlderThanAsync(DateTime.UtcNow.AddDays(-31));          // 경계 바깥은 허용
    }

    [Fact]
    public async Task Purge_keeps_running_jobs_and_audit_log()
    {
        using var d = new TempDir();
        var db = System.IO.Path.Combine(d.Path, "t.db");
        var repo = new SqliteResultRepository(db);
        var acc = new SqliteAccountRepository(db);
        var running = await repo.BeginJobAsync(new ModelInfo("m", "v", "c"), "s", 0.5f);       // 상태 Running, 결과 없음
        await acc.AppendAsync(new AuditEntry(0, DateTime.UtcNow.AddDays(-400), null, "kim", AuditActions.LoginSuccess, null));

        var res = await repo.PurgeOlderThanAsync(DateTime.UtcNow.AddDays(-60));
        Assert.Equal(0, res.Jobs);                                                              // 진행 중 회차는 지우지 않음
        Assert.Single(await acc.QueryAsync(new AuditFilter()));                                 // 감사 로그는 오래돼도 유지
    }

    [Fact]
    public async Task Purge_nothing_to_delete_is_a_noop()
    {
        using var d = new TempDir();
        var (repo, _, _) = await Seed(d.Path);
        var res = await repo.PurgeOlderThanAsync(DateTime.UtcNow.AddDays(-365));
        Assert.Equal((0, 0), (res.Results, res.Jobs));
        Assert.Equal(5, (await repo.QueryAsync(new HistoryFilter())).Count);
    }
}
