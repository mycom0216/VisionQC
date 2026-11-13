using VisionQC.Core;

namespace VisionQC.Tests;

public class AutoBackupSettingsTests
{
    [Fact]
    public void Default_is_24_hours_and_roundtrips_including_off()
    {
        Assert.Equal(24, new AppSettings().AutoBackupHours);
        using var d = new TempDir();
        var store = new SettingsStore(System.IO.Path.Combine(d.Path, "s.json"));
        store.Save(new AppSettings { AutoBackupHours = 0 });                    // 0 = 끔
        Assert.Equal(0, store.Load().Settings.AutoBackupHours);
        store.Save(new AppSettings { AutoBackupHours = 720 });
        Assert.Equal(720, store.Load().Settings.AutoBackupHours);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(721)]
    public void Out_of_range_in_file_is_reset_with_warning_and_save_is_rejected(int hours)
    {
        using var d = new TempDir();
        var p = System.IO.Path.Combine(d.Path, "s.json");
        File.WriteAllText(p, $$"""{"autoBackupHours": {{hours}}}""");
        var (s, w) = new SettingsStore(p).Load();
        Assert.Equal(24, s.AutoBackupHours);
        Assert.Contains("자동 백업", w);
        Assert.Throws<ArgumentException>(() => new SettingsStore(p).Save(new AppSettings { AutoBackupHours = hours }));
    }

    [Fact]
    public void Old_file_without_the_key_gets_default()
    {
        using var d = new TempDir();
        var p = System.IO.Path.Combine(d.Path, "s.json");
        File.WriteAllText(p, """{"threshold": 0.4}""");
        var (s, w) = new SettingsStore(p).Load();
        Assert.Equal(24, s.AutoBackupHours);
        Assert.Null(w);
    }
}
