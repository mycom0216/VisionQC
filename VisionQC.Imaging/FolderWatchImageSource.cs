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
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff" };

    private readonly string _folder;
    private readonly bool _includeExisting;

    public TimeSpan PollInterval { get; }
    public TimeSpan StableTimeout { get; }

    /// <param name="includeExisting">true면 시작 시점에 이미 있던 파일도 검사, false면 그 뒤에 생긴 파일만.</param>
    public FolderWatchImageSource(string folder, bool includeExisting = false, TimeSpan? pollInterval = null, TimeSpan? stableTimeout = null)
    {
        _folder = folder;
        _includeExisting = includeExisting;
        PollInterval = pollInterval ?? TimeSpan.FromMilliseconds(500);
        StableTimeout = stableTimeout ?? TimeSpan.FromSeconds(30);
    }

    public string Name => $"watch:{_folder}";

    private sealed class Pending
    {
        public long Size; public long WriteTicks; public int StablePolls; public DateTime FirstSeenUtc;
    }

    public async IAsyncEnumerable<ImageFrame> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (!Directory.Exists(_folder))
            throw new DirectoryNotFoundException($"폴더를 찾을 수 없음: {_folder}");

        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // 이미 내보냈거나(또는 무시하기로 한) 파일
        if (!_includeExisting)
            foreach (var f in List()) done.Add(f.FullName);

        var pending = new Dictionary<string, Pending>(StringComparer.OrdinalIgnoreCase);
        int listFailures = 0;

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
                var ready = new List<FileInfo>();
                var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var f in files)
                {
                    present.Add(f.FullName);
                    if (done.Contains(f.FullName)) continue;

                    if (!pending.TryGetValue(f.FullName, out var p))
                        pending[f.FullName] = p = new Pending { Size = f.Length, WriteTicks = f.LastWriteTimeUtc.Ticks, FirstSeenUtc = now };
                    else if (p.Size == f.Length && p.WriteTicks == f.LastWriteTimeUtc.Ticks) p.StablePolls++;
                    else { p.Size = f.Length; p.WriteTicks = f.LastWriteTimeUtc.Ticks; p.StablePolls = 0; }

                    bool stable = p.StablePolls >= 1 && CanOpenExclusively(f.FullName);
                    if (stable || now - p.FirstSeenUtc >= StableTimeout) ready.Add(f);
                }

                // 사라진 파일은 대기 목록에서 제거(복사하다 취소된 경우 등)
                foreach (var gone in pending.Keys.Where(k => !present.Contains(k)).ToList()) pending.Remove(gone);

                foreach (var f in ready.OrderBy(x => x.LastWriteTimeUtc).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                {
                    pending.Remove(f.FullName);
                    done.Add(f.FullName);
                    yield return new ImageFrame(f.FullName);
                }
            }

            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
    }

    private List<FileInfo> List()
        => new DirectoryInfo(_folder).EnumerateFiles().Where(f => Extensions.Contains(f.Extension)).ToList();

    /// <summary>다른 프로세스가 아직 쓰는 중이면(열려 있는 핸들이 있으면) 단독 열기가 실패한다.</summary>
    private static bool CanOpenExclusively(string path)
    {
        try { using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
