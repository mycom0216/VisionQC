// ============================================================================
// FolderWatchImageSource.cs
// 역할: 폴더를 감시하며 새로 생기는 이미지를 계속 공급하는 이미지 소스(라인에서 카메라가 폴더에 저장하는 연속 검사용).
// 파이프라인에서의 위치:
//   [이미지 소스: 여기] -> 전처리 -> 추론 -> 판정(OK/NG/ERROR) -> 이력 저장소 / UI
// 핵심 문제: 파일이 "생긴 순간"에는 아직 복사/저장이 끝나지 않았을 수 있다. 반쯤 쓰인 이미지를 읽으면 손상 이미지로 오판(ERROR)되므로
//   크기·수정시각이 더 이상 변하지 않을 때까지 기다리는 "안정성 검사(debounce)"를 한 뒤에 내보낸다.
// ============================================================================
using System.Runtime.CompilerServices;
using VisionQC.Core;

namespace VisionQC.Imaging;

/// <summary>
/// 폴더에 새로 생기는 이미지를 계속 공급한다(연속 검사). 취소될 때까지 끝나지 않는다.
///
/// FileSystemWatcher 대신 주기적 폴링을 쓴다: 이벤트 유실(버퍼 넘침·네트워크 드라이브)이 없고 동작이 예측 가능하다.
/// 대신 새 파일은 "폴링 간격 × 2" 정도 늦게 잡힌다(기본 약 1초) — 라인 촬영 주기보다 충분히 짧다고 가정한 선택이다.
///
/// 복사·저장 중인 파일을 읽지 않도록, 두 번 연속 크기·수정시각이 같고 단독으로 열 수 있을 때만 내보낸다.
/// 끝내 안정되지 않는 파일은 <see cref="StableTimeout"/> 뒤에 그대로 내보내 ERROR로 기록되게 한다(조용히 빠지지 않게).
/// 큰 폴더(수만 장)에서는 폴링 비용이 커지므로 처리 끝난 파일을 다른 폴더로 옮기는 운용을 권한다.
/// </summary>
public sealed class FolderWatchImageSource : IImageSource
{
    // 검사 대상 확장자(대소문자 무시)
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff" };

    private readonly string _folder;
    private readonly bool _includeExisting;

    /// <summary>폴더를 다시 확인하는 간격(기본 500ms). 파일이 안정되었다고 보려면 연속 두 번 같아야 하므로 새 파일은 대략 이 간격의 2배 뒤에 잡힌다.</summary>
    public TimeSpan PollInterval { get; }
    /// <summary>한 파일이 처음 보인 뒤 이 시간(기본 30초)이 지나도 안정되지 않으면 포기하고 그대로 내보낸다(→ 읽기 실패 시 ERROR로 기록).</summary>
    public TimeSpan StableTimeout { get; }

    /// <param name="folder">감시할 폴더(하위 폴더는 보지 않는다)</param>
    /// <param name="pollInterval">폴링 간격(기본 500ms)</param>
    /// <param name="stableTimeout">안정화 대기 한도(기본 30초)</param>
    /// <param name="includeExisting">true면 시작 시점에 이미 있던 파일도 검사, false면 그 뒤에 생긴 파일만.</param>
    public FolderWatchImageSource(string folder, bool includeExisting = false, TimeSpan? pollInterval = null, TimeSpan? stableTimeout = null)
    {
        _folder = folder;
        _includeExisting = includeExisting;
        PollInterval = pollInterval ?? TimeSpan.FromMilliseconds(500);
        StableTimeout = stableTimeout ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>이력(회차)에 기록되는 소스 이름.</summary>
    public string Name => $"watch:{_folder}";

    /// <summary>
    /// 아직 "안정화 확인 중"인 파일의 상태. 직전 폴링의 크기/수정시각(Ticks)과 같았던 횟수(StablePolls), 처음 발견한 시각을 가진다.
    /// </summary>
    private sealed class Pending
    {
        public long Size; public long WriteTicks; public int StablePolls; public DateTime FirstSeenUtc;
    }

    /// <summary>
    /// 폴더를 계속 폴링하며 안정화된 새 파일을 하나씩 내보낸다. 취소(ct)될 때까지 끝나지 않는다.
    /// 흐름: 목록 조회 → 새 파일은 대기 목록(pending)에 등록 → 크기·수정시각이 이전 폴링과 같으면 카운트 증가, 다르면 초기화
    ///       → 안정(1회 이상 동일 + 단독으로 열기 가능)하거나 대기 한도 초과면 내보냄 → 오래된 수정시각 순으로 yield → 간격만큼 대기.
    /// </summary>
    public async IAsyncEnumerable<ImageFrame> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (!Directory.Exists(_folder))
            throw new DirectoryNotFoundException($"폴더를 찾을 수 없음: {_folder}");

        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // 이미 내보냈거나(또는 무시하기로 한) 파일
        // 시작 시점에 이미 있던 파일을 "처리 끝"으로 미리 표시해 두면, 이후 새로 생긴 파일만 검사된다.
        if (!_includeExisting)
            foreach (var f in List()) done.Add(f.FullName);

        // 안정화 대기 중인 파일들(경로 → 상태)
        var pending = new Dictionary<string, Pending>(StringComparer.OrdinalIgnoreCase);
        int listFailures = 0;   // 폴더 목록 조회가 연속으로 실패한 횟수

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            List<FileInfo>? files = null;
            try { files = List(); listFailures = 0; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 폴더가 잠깐 사라지거나(네트워크 끊김) 접근이 막힐 수 있다 — 연속 10번 실패하면 치명적 오류로 본다
                if (++listFailures >= 10) throw new IOException($"감시 폴더를 읽을 수 없음: {_folder} ({ex.Message})", ex);
            }

            if (files is not null)
            {
                var now = DateTime.UtcNow;
                var ready = new List<FileInfo>();   // 이번 폴링에서 내보낼 파일
                var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var f in files)
                {
                    present.Add(f.FullName);
                    if (done.Contains(f.FullName)) continue;

                    // 처음 보는 파일이면 현재 크기/수정시각을 기록, 지난번과 같으면 "안정 횟수" +1, 다르면(아직 쓰는 중) 값을 갱신하고 횟수를 0으로
                    if (!pending.TryGetValue(f.FullName, out var p))
                        pending[f.FullName] = p = new Pending { Size = f.Length, WriteTicks = f.LastWriteTimeUtc.Ticks, FirstSeenUtc = now };
                    else if (p.Size == f.Length && p.WriteTicks == f.LastWriteTimeUtc.Ticks) p.StablePolls++;
                    else { p.Size = f.Length; p.WriteTicks = f.LastWriteTimeUtc.Ticks; p.StablePolls = 0; }

                    // 안정 = 연속 두 번 같은 크기·시각 + 다른 프로세스가 쓰기 핸들을 쥐고 있지 않음. 한도(StableTimeout)를 넘기면 어쨌든 내보낸다.
                    bool stable = p.StablePolls >= 1 && CanOpenExclusively(f.FullName);
                    if (stable || now - p.FirstSeenUtc >= StableTimeout) ready.Add(f);
                }

                // 사라진 파일은 대기 목록에서 제거(복사하다 취소된 경우 등)
                foreach (var gone in pending.Keys.Where(k => !present.Contains(k)).ToList()) pending.Remove(gone);

                // 오래된 파일부터(수정시각 → 이름 순) 내보내 촬영 순서를 최대한 유지한다. 내보낸 파일은 done에 넣어 다시 검사하지 않는다.
                foreach (var f in ready.OrderBy(x => x.LastWriteTimeUtc).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                {
                    pending.Remove(f.FullName);
                    done.Add(f.FullName);
                    yield return new ImageFrame(f.FullName);
                }
            }

            // 다음 폴링까지 대기(취소 신호가 오면 여기서 즉시 깨어나 OperationCanceledException으로 끝난다)
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>감시 폴더 바로 아래의 이미지 확장자 파일 목록(하위 폴더 제외). 폴더 접근 오류는 호출부에서 처리한다.</summary>
    private List<FileInfo> List()
        => new DirectoryInfo(_folder).EnumerateFiles().Where(f => Extensions.Contains(f.Extension)).ToList();

    /// <summary>다른 프로세스가 아직 쓰는 중이면(열려 있는 핸들이 있으면) 단독 열기가 실패한다. FileShare.None으로 열어 보고 곧바로 닫는다(읽지 않는다).</summary>
    private static bool CanOpenExclusively(string path)
    {
        try { using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
