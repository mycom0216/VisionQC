// ============================================================================================
// HistoryViewModel.cs — "이력" 탭(HistoryTab)의 ViewModel (FR-08).
//
// 하는 일: DB에 저장된 검사 결과(원 판정)를 기간·판정(OK/NG/ERROR)으로 조회해 표로 보여 주고, 선택한 행의 이미지(NG는 히트맵 포함)와
//   상세를 오른쪽에 보여 주며, 표시된 결과를 CSV로 내보낸다.
//   노출하는 명령: RefreshCommand(조회), ExportCsvCommand(CSV 내보내기).
//   정정(재판정) 결과는 포함하지 않는다 — 그것은 재판정 탭(ReviewViewModel)의 몫이다.
// ============================================================================================
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisionQC.Core;

namespace VisionQC.App.ViewModels;

/// <summary>이력 탭의 ViewModel. 읽기 전용 조회 화면이라 별도 권한이 필요 없다(모든 로그인 사용자).</summary>
public partial class HistoryViewModel : ObservableObject
{
    private readonly IResultRepository _repo;
    public HistoryViewModel(IResultRepository repo)
    {
        _repo = repo;
        // 히트맵 표시를 켜고 끄면 선택한 이미지를 다시 그린다(약한 참조 — 로그아웃 후 이전 화면이 남지 않게)
        // 주의: 약한 이벤트 관리자에는 람다가 아니라 인스턴스 메서드를 넘겨야 한다(람다는 곧 수거돼 핸들러가 조용히 사라진다)
        System.ComponentModel.PropertyChangedEventManager.AddHandler(ViewOptions.Instance, OnViewOptionsChanged, nameof(ViewOptions.ShowHeatmap));
    }

    /// <summary>히트맵 체크박스가 바뀌면 선택한 이미지를 다시 그린다.</summary>
    private void OnViewOptionsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => RedrawImage();

    /// <summary>선택한 결과의 이미지를 현재 옵션으로 다시 합성한다(히트맵은 NG 결과에만 있다).</summary>
    private void RedrawImage()
    {
        if (Selected is { } s) SelectedImage = HeatmapRenderer.Compose(s.ImagePath, s.Verdict == Verdict.NG ? s.Heatmap : null, ViewOptions.Instance.ShowHeatmap);
    }

    /// <summary>판정 필터 콤보박스 항목("전체"는 필터 없음).</summary>
    public string[] VerdictOptions { get; } = { "전체", "OK", "NG", "ERROR" };

    // 조회 조건(판정, 시작·종료 날짜)과 상태 줄 메시지.
    [ObservableProperty] private string _selectedVerdict = "전체";
    [ObservableProperty] private DateTime? _fromDate;
    [ObservableProperty] private DateTime? _toDate;
    [ObservableProperty] private string _message = "";
    // 표에서 선택한 결과, 그 이미지(히트맵 합성), 상세 글자.
    [ObservableProperty] private InspectionResult? _selected;
    [ObservableProperty] private ImageSource? _selectedImage;
    [ObservableProperty] private string _selectedDetail = "";

    /// <summary>표에 바인딩된 조회 결과 목록(최신 500건까지).</summary>
    public ObservableCollection<InspectionResult> Items { get; } = new();

    // 선택이 바뀌면 이미지와 상세를 채운다. 파일이 이동·삭제되어 이미지를 못 열면 그 사실을 상세에 덧붙인다(DB 기록은 남아 있음).
    partial void OnSelectedChanged(InspectionResult? value)
    {
        if (value is null) { SelectedImage = null; SelectedDetail = ""; return; }
        SelectedImage = HeatmapRenderer.Compose(value.ImagePath, value.Verdict == Verdict.NG ? value.Heatmap : null, ViewOptions.Instance.ShowHeatmap);
        var local = value.InspectedAtUtc.ToLocalTime();
        SelectedDetail = $"{value.ImagePath}\n{local:yyyy-MM-dd HH:mm:ss} · {value.Verdict} · 점수 {value.Score:F3} · {value.ElapsedMs} ms"
            + (SelectedImage is null ? "\n※ 이미지 파일을 열 수 없음(이동·삭제됨?)" : "")
            + (value.ErrorMessage is null ? "" : $"\n{value.ErrorMessage}");
    }

    /// <summary>"조회" 버튼(탭을 열 때도 호출). 조건에 맞는 최신 500건을 DB에서 읽어 목록을 채운다. 실패는 예외 대신 Message로 표시한다.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            // 입력한 날짜는 로컬 기준. To는 그날 끝까지 포함.
            var (from, to) = HistoryPeriod.ToUtc(FromDate, ToDate, TimeZoneInfo.Local);
            Verdict? v = SelectedVerdict == "전체" ? null : Enum.Parse<Verdict>(SelectedVerdict);

            // 500건 상한: 화면이 무거워지지 않게 한다. 꽉 차면 "최신 500건만 표시" 안내를 띄운다.
            var rows = await _repo.QueryAsync(new HistoryFilter(from, to, v, 500));
            Items.Clear();
            foreach (var r in rows) Items.Add(r);
            Message = rows.Count == 500 ? "최신 500건만 표시합니다." : $"{rows.Count}건";
        }
        catch (Exception ex) { Message = $"조회 실패: {ex.Message}"; }
    }

    /// <summary>
    /// "표시 결과 CSV" 버튼. 화면에 보이는 원 판정을 CSV 파일로 저장한다(정정 판정은 미포함).
    /// 임시 파일에 먼저 쓴 뒤 목적 파일로 옮겨(File.Move) 쓰는 도중 실패해도 기존 파일이 깨지지 않게 한다. BOM이 있는 UTF-8이라 Excel에서 한글이 깨지지 않는다.
    /// </summary>
    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        // 화면의 조회 결과를 고정한다. 조회 후 필터만 바꿔도 다른 데이터로 내보내지 않는다.
        var snapshot = Items.ToArray();
        if (snapshot.Length == 0) { Message = "먼저 이력을 조회하세요. 내보낼 결과가 없습니다."; return; }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "현재 표시된 원 판정 내보내기 (최대 500건)", Filter = "CSV 파일 (*.csv)|*.csv",
            FileName = $"VisionQC_history_{DateTime.Now:yyyyMMdd_HHmmss}.csv", DefaultExt = ".csv", AddExtension = true
        };
        if (dialog.ShowDialog() != true) return;
        string? temp = null;
        try
        {
            temp = dialog.FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
            // 파일 쓰기는 백그라운드 스레드에서 수행해 UI가 멈추지 않게 한다.
            await Task.Run(() =>
            {
                using var writer = new StreamWriter(temp, false, new UTF8Encoding(true));
                HistoryCsv.Write(writer, snapshot);
            });
            File.Move(temp, dialog.FileName, overwrite: true);
            Message = $"현재 표시된 원 판정 {snapshot.Length}건을 CSV로 저장했습니다(최대 500건).";
        }
        catch (Exception ex) { Message = $"CSV 저장 실패: {ex.Message}"; }
        finally { if (temp is not null) { try { File.Delete(temp); } catch { /* 원래 실패 메시지를 유지 */ } } }
    }
}
