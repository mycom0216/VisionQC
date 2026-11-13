using System.Globalization;
using System.IO;
using System.Windows.Data;

namespace VisionQC.App;

public sealed class InvertBoolConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is bool b ? !b : true;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is bool b ? !b : false;
}

public sealed class FileNameConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => Path.GetFileName(value as string ?? "");
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class LocalTimeConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
        => value is DateTime d ? d.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "";
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
