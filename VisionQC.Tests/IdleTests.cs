using VisionQC.Core;

namespace VisionQC.Tests;

public class IdleMonitorTests
{
    private sealed class Clock { public DateTime T = new(2026, 11, 13, 6, 0, 0, DateTimeKind.Utc); public DateTime Now() => T; }

    [Fact]
    public void Expires_after_timeout_without_activity()
    {
        var c = new Clock();
        var m = new IdleMonitor(TimeSpan.FromMinutes(15), c.Now);
        c.T += TimeSpan.FromMinutes(14) + TimeSpan.FromSeconds(59);
        Assert.False(m.IsExpired(busy: false));
        c.T += TimeSpan.FromSeconds(1);                      // 정확히 15분 = 만료(경계 포함)
        Assert.True(m.IsExpired(busy: false));
    }

    [Fact]
    public void Touch_resets_the_countdown()
    {
        var c = new Clock();
        var m = new IdleMonitor(TimeSpan.FromMinutes(15), c.Now);
        c.T += TimeSpan.FromMinutes(10);
        m.Touch();
        c.T += TimeSpan.FromMinutes(10);                     // 마지막 조작 후 10분
        Assert.False(m.IsExpired(false));
        c.T += TimeSpan.FromMinutes(5);
        Assert.True(m.IsExpired(false));
    }

    [Fact]
    public void Never_expires_while_busy_and_restarts_counting_when_busy_ends()
    {
        var c = new Clock();
        var m = new IdleMonitor(TimeSpan.FromMinutes(15), c.Now);
        c.T += TimeSpan.FromHours(3);                        // 3시간 동안 검사·감시 중
        Assert.False(m.IsExpired(busy: true));
        Assert.False(m.IsExpired(busy: false));              // 끝난 직후엔 바로 만료되지 않고
        c.T += TimeSpan.FromMinutes(15);
        Assert.True(m.IsExpired(busy: false));               // 그때부터 15분 뒤 만료
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Timeout_must_be_positive(int minutes)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new IdleMonitor(TimeSpan.FromMinutes(minutes)));

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(15, true)]
    [InlineData(480, true)]
    [InlineData(481, false)]
    public void Valid_minutes_range(int minutes, bool valid) => Assert.Equal(valid, IdleMonitor.IsValidMinutes(minutes));

    [Fact]
    public void Settings_default_is_15_minutes_and_roundtrips()
    {
        Assert.Equal(15, new AppSettings().IdleTimeoutMinutes);
        using var d = new TempDir();
        var store = new SettingsStore(System.IO.Path.Combine(d.Path, "s.json"));
        store.Save(new AppSettings { IdleTimeoutMinutes = 30 });
        Assert.Equal(30, store.Load().Settings.IdleTimeoutMinutes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(999)]
    public void Out_of_range_idle_in_file_is_reset_to_default_with_warning(int minutes)
    {
        using var d = new TempDir();
        var p = System.IO.Path.Combine(d.Path, "s.json");
        File.WriteAllText(p, $$"""{"idleTimeoutMinutes": {{minutes}}}""");
        var (s, w) = new SettingsStore(p).Load();
        Assert.Equal(IdleMonitor.DefaultMinutes, s.IdleTimeoutMinutes);
        Assert.Contains("자동 로그아웃", w);
    }

    [Fact]
    public void Old_settings_file_without_the_key_gets_default()
    {
        using var d = new TempDir();
        var p = System.IO.Path.Combine(d.Path, "s.json");
        File.WriteAllText(p, """{"threshold": 0.4, "modelPath": "m.onnx"}""");     // 이전 버전이 만든 파일
        var (s, w) = new SettingsStore(p).Load();
        Assert.Equal(15, s.IdleTimeoutMinutes);
        Assert.Null(w);
    }

    [Fact]
    public void Save_rejects_out_of_range_idle()
    {
        using var d = new TempDir();
        var store = new SettingsStore(System.IO.Path.Combine(d.Path, "s.json"));
        Assert.Throws<ArgumentException>(() => store.Save(new AppSettings { IdleTimeoutMinutes = 0 }));
        Assert.Throws<ArgumentException>(() => store.Save(new AppSettings { IdleTimeoutMinutes = 481 }));
    }
}
