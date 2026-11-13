using System.IO;
using System.Windows;
using System.Windows.Controls;
using VisionQC.App.ViewModels;
using VisionQC.Data;

namespace VisionQC.App;

public partial class MainWindow : Window
{
    private readonly HistoryViewModel? _history;
    private readonly StatsViewModel? _stats;
    private bool _autoRunDone;

    public MainWindow()
    {
        InitializeComponent();

        // 선택적 실행 인자(시연·자동화용): --folder <경로> --model <onnx> --threshold <0~1> --db <파일> --autorun --tab history|stats
        var args = ParseArgs(Environment.GetCommandLineArgs().Skip(1).ToArray());

        var dbPath = args.GetValueOrDefault("db") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisionQC", "visionqc.db");
        SqliteResultRepository repo;
        try { repo = new SqliteResultRepository(dbPath); }
        catch (Exception ex)
        {
            MessageBox.Show($"이력 DB를 열 수 없습니다.\n{dbPath}\n\n{ex.Message}", "VisionQC", MessageBoxButton.OK, MessageBoxImage.Error);
            Application.Current.Shutdown();
            return;
        }

        var inspection = new InspectionViewModel(repo);
        if (args.TryGetValue("folder", out var folder)) inspection.FolderPath = folder;
        if (args.TryGetValue("model", out var model)) inspection.ModelPath = model;
        if (args.TryGetValue("threshold", out var th) &&
            double.TryParse(th, System.Globalization.CultureInfo.InvariantCulture, out var thv))
            inspection.Threshold = Math.Clamp(thv, 0, 1);

        InspectionTab.DataContext = inspection;
        _history = new HistoryViewModel(repo);
        HistoryTab.DataContext = _history;
        _stats = new StatsViewModel(repo);
        StatsTab.DataContext = _stats;

        var startTab = args.GetValueOrDefault("tab");
        var autorun = args.ContainsKey("autorun");
        if (autorun || startTab is not null)
            Loaded += async (_, _) =>
            {
                // Loaded는 창이 비주얼 트리에 다시 붙을 때 또 발생할 수 있어 한 번만 실행한다.
                if (_autoRunDone) return;
                _autoRunDone = true;
                if (autorun) await inspection.StartCommand.ExecuteAsync(null);
                if (startTab == "history") Tabs.SelectedItem = HistoryTab;
                else if (startTab == "stats") Tabs.SelectedItem = StatsTab;
            };
    }

    private static Dictionary<string, string> ParseArgs(string[] argv)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < argv.Length; i++)
        {
            if (!argv[i].StartsWith("--")) continue;
            var key = argv[i][2..];
            d[key] = i + 1 < argv.Length && !argv[i + 1].StartsWith("--") ? argv[++i] : "";
        }
        return d;
    }

    private async void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // TabControl 안의 ComboBox 등에서 올라온 SelectionChanged는 무시
        if (e.OriginalSource != Tabs || _history is null || _stats is null) return;
        if (Tabs.SelectedItem == HistoryTab) await _history.RefreshAsync();
        else if (Tabs.SelectedItem == StatsTab) await _stats.RefreshAsync();
    }
}
