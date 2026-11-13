using System.IO;
using System.Windows;
using System.Windows.Controls;
using VisionQC.App.ViewModels;
using VisionQC.Data;

namespace VisionQC.App;

public partial class MainWindow : Window
{
    private readonly HistoryViewModel? _history;

    public MainWindow()
    {
        InitializeComponent();

        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisionQC", "visionqc.db");
        SqliteResultRepository repo;
        try { repo = new SqliteResultRepository(dbPath); }
        catch (Exception ex)
        {
            MessageBox.Show($"이력 DB를 열 수 없습니다.\n{dbPath}\n\n{ex.Message}", "VisionQC", MessageBoxButton.OK, MessageBoxImage.Error);
            Application.Current.Shutdown();
            return;
        }

        InspectionTab.DataContext = new InspectionViewModel(repo);
        _history = new HistoryViewModel(repo);
        HistoryTab.DataContext = _history;
    }

    private async void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // TabControl 안의 ComboBox 등에서 올라온 SelectionChanged는 무시
        if (e.OriginalSource != Tabs || _history is null) return;
        if (Tabs.SelectedItem == HistoryTab) await _history.RefreshAsync();
    }
}
