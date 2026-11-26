// ============================================================================
// FolderImageSource.cs
// 역할: 지정한 폴더 안의 이미지 파일을 "한 번 훑어" 차례대로 공급하는 이미지 소스(일괄 검사용).
// 파이프라인에서의 위치:
//   [이미지 소스: 여기] -> 전처리 -> 추론 -> 판정(OK/NG/ERROR) -> 이력 저장소 / UI
//   IImageSource(Core)를 구현하므로 파이프라인은 폴더인지 카메라인지 몰라도 된다. 파일을 모두 내보내면 정상 종료한다.
//   (계속 새 파일을 기다리는 방식은 FolderWatchImageSource, 영상/카메라는 VideoCaptureImageSource.)
// ============================================================================
using System.Runtime.CompilerServices;
using VisionQC.Core;

namespace VisionQC.Imaging;

/// <summary>폴더의 이미지 파일을 이름순으로 공급한다. 폴더가 없으면 읽기 시작 시 예외(→ 작업 Failed).</summary>
public sealed class FolderImageSource : IImageSource
{
    // 검사 대상으로 인정하는 확장자(대소문자 무시). 그 외 파일(텍스트, 썸네일 DB 등)은 무시한다.
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff" };

    private readonly string _folder;
    private readonly bool _recursive;

    /// <param name="folder">이미지가 들어 있는 폴더 경로</param>
    /// <param name="recursive">true면 하위 폴더까지 포함, false면 해당 폴더 바로 아래 파일만</param>
    public FolderImageSource(string folder, bool recursive = false)
    {
        _folder = folder;
        _recursive = recursive;
    }

    /// <summary>이력(회차)에 기록되는 소스 이름. 어느 폴더를 검사했는지 사람이 알아볼 수 있게 한다.</summary>
    public string Name => $"folder:{_folder}";

    /// <summary>
    /// 폴더의 이미지를 하나씩 비동기로 내보낸다(IAsyncEnumerable: 호출자가 await foreach로 받는다).
    /// 파일 목록은 이름순(대소문자 무시)으로 정렬해 매번 같은 순서로 검사하므로 결과를 비교·재현하기 쉽다.
    /// 여기서는 경로만 담은 ImageFrame을 만들고, 실제 이미지 읽기는 다음 단계인 전처리기가 한다.
    /// </summary>
    /// <param name="ct">취소 토큰(사용자가 "중지"를 누르면 신호가 온다)</param>
    public async IAsyncEnumerable<ImageFrame> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (!Directory.Exists(_folder))
            throw new DirectoryNotFoundException($"폴더를 찾을 수 없음: {_folder}");

        // 목록은 지연 실행(lazy)이라 폴더 전체를 메모리에 모으지 않고 필요할 때 열거한다.
        var option = _recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = Directory.EnumerateFiles(_folder, "*", option)
            .Where(f => Extensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();     // 중지 요청이 있으면 즉시 끝낸다
            yield return new ImageFrame(f);
            await Task.Yield();                    // 파일이 많아도 호출 스레드(UI)를 오래 붙잡지 않도록 제어권을 양보
        }
    }
}
