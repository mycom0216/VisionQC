using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisionQC.Core;

namespace VisionQC.App.ViewModels;

public partial class HistoryViewModel : ObservableObject
{
    private readonly IResultRepository _repo;
    public HistoryViewModel(IResultRepository repo) => _repo = repo;

    public string[] VerdictOptions { get; } = { "전체", "OK", "NG", "ERROR" };

    [ObservableProperty] private string _selectedVerdict = "전체";
    [ObservableProperty] private DateTime? _fromDate;
    [ObservableProperty] private DateTime? _toDate;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private InspectionResult? _selected;
    [ObservableProperty] private ImageSource? _selectedImage;
    [ObservableProperty] private string _selectedDetail = "";

    public ObservableCollection<InspectionResult> Items { get; } = new();

    partial void OnSelectedChanged(InspectionResult? value)
    {
        if (value is null) { SelectedImage = null; SelectedDetail = ""; return; }
        SelectedImage = ImageLoader.Load(value.ImagePath);
        var local = value.InspectedAtUtc.ToLocalTime();
        SelectedDetail = $"{value.ImagePath}\n{local:yyyy-MM-dd HH:mm:ss} · {value.Verdict} · 점수 {value.Score:F3} · {value.ElapsedMs} ms"
            + (SelectedImage is null ? "\n※ 이미지 파일을 열 수 없음(이동·삭제됨?)" : "")
            + (value.ErrorMessage is null ? "" : $"\n{value.ErrorMessage}");
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            // 입력한 날짜는 로컬 기준. To는 그날 끝까지 포함.
            DateTime? from = FromDate is { } f ? f.Date.ToUniversalTime() : null;
            DateTime? to = ToDate is { } t ? t.Date.AddDays(1).AddTicks(-1).ToUniversalTime() : null;
            Verdict? v = SelectedVerdict == "전체" ? null : Enum.Parse<Verdict>(SelectedVerdict);

            var rows = await _repo.QueryAsync(new HistoryFilter(from, to, v, 500));
            Items.Clear();
            foreach (var r in rows) Items.Add(r);
            Message = rows.Count == 500 ? "최신 500건만 표시합니다." : $"{rows.Count}건";
        }
        catch (Exception ex) { Message = $"조회 실패: {ex.Message}"; }
    }
}
