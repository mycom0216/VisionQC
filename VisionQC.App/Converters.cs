// ============================================================================================
// Converters.cs — XAML 바인딩에서 쓰는 값 변환기(IValueConverter) 모음.
//
// 바인딩된 ViewModel 값을 화면에 맞는 형태로 바꿔 준다. App.xaml의 Application.Resources에 키(InvertBool, FileName, LocalTime)로
// 등록되어 있어 MainWindow.xaml 어디서든 {StaticResource ...}로 쓴다. ViewModel을 화면 표현 때문에 더럽히지 않기 위한 장치다.
// ============================================================================================
using System.Globalization;
using System.IO;
using System.Windows.Data;

namespace VisionQC.App;

/// <summary>bool을 반대로 뒤집는다. 예: "검사 중(IsRunning)이면 입력 모드 콤보박스를 비활성(IsEnabled=false)"으로 만들 때 쓴다. bool이 아니면 true.</summary>
public sealed class InvertBoolConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is bool b ? !b : true;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is bool b ? !b : false;
}

/// <summary>전체 경로에서 파일 이름만 뽑는다(목록 칸을 짧게 보여 주기 위함). 한 방향 변환이라 ConvertBack은 지원하지 않는다.</summary>
public sealed class FileNameConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => Path.GetFileName(value as string ?? "");
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>
/// DB에는 UTC로 저장된 시각(DateTime)을 사용자 PC의 로컬 시간 "yyyy-MM-dd HH:mm:ss" 문자열로 바꾼다(이력 탭 "시각" 열).
/// 값이 DateTime이 아니면 빈 문자열. 문화권에 따라 모양이 바뀌지 않도록 InvariantCulture를 쓴다.
/// </summary>
public sealed class LocalTimeConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
        => value is DateTime d ? d.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "";
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
