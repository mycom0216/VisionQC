using System.Runtime.CompilerServices;
using VisionQC.Core;

namespace VisionQC.Imaging;

/// <summary>폴더의 이미지 파일을 이름순으로 공급한다. 폴더가 없으면 읽기 시작 시 예외(→ 작업 Failed).</summary>
public sealed class FolderImageSource : IImageSource
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff" };

    private readonly string _folder;
    private readonly bool _recursive;

    public FolderImageSource(string folder, bool recursive = false)
    {
        _folder = folder;
        _recursive = recursive;
    }

    public string Name => $"folder:{_folder}";

    public async IAsyncEnumerable<ImageFrame> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (!Directory.Exists(_folder))
            throw new DirectoryNotFoundException($"폴더를 찾을 수 없음: {_folder}");

        var option = _recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = Directory.EnumerateFiles(_folder, "*", option)
            .Where(f => Extensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            yield return new ImageFrame(f);
            await Task.Yield();
        }
    }
}
