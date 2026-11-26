// ============================================================================
// Program.cs (VisionQC.Bench) - 성능·메모리 측정용 콘솔 도구(제품 앱과는 별도 실행 파일)
//
// 목적: 실제 앱과 같은 부품(OpenCV 전처리 -> ONNX 추론 -> 임계값 판정 -> 파이프라인)을 그대로 써서
//   A) 이미지 1장당 단계별 처리시간(순차 실행)
//   B) 워커 수별 파이프라인 처리량(저장소 없이)
//   C) 반복 실행 중 메모리 변화(네이티브 메모리 누수 의심 여부)
// 를 재고 Markdown 보고서로 출력한다. UI·DB 없이 순수하게 계산 성능만 보려고 가짜 저장소(NullRepository)를 쓴다.
// 이 파일은 최상위 문장(top-level statements) 방식이라 Main 메서드 없이 위에서 아래로 실행된다.
// ============================================================================
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using VisionQC.Core;
using VisionQC.Imaging;
using VisionQC.Inference;

// 성능·메모리 측정 도구. 사용:
//   dotnet run -c Release --project VisionQC.Bench -- --folder <이미지폴더> --model <model.onnx> [--repeat 5] [--threshold 0.5] [--out result.md] [--cpu "CPU 이름"]
// 결과는 이 PC·이 모델·이 이미지에서의 값이다. 다른 환경으로 일반화하지 않는다.

// ── 인자 처리: --folder 와 --model 은 필수. 없으면 종료 코드 2(사용법 오류)로 끝낸다. ──
var a = ParseArgs(args);
if (!a.TryGetValue("folder", out var folder) || !a.TryGetValue("model", out var modelPath))
{
    Console.Error.WriteLine("필수 인자: --folder <이미지폴더> --model <model.onnx>");
    return 2;
}
// 반복 횟수는 2 이상이어야 "1회차 대비 변화"(메모리)를 비교할 수 있어 기본 5로 보정한다. 임계값은 문화권 무관(InvariantCulture)으로 파싱한다.
int repeat = a.TryGetValue("repeat", out var rs) && int.TryParse(rs, out var r) && r >= 2 ? r : 5;
float threshold = a.TryGetValue("threshold", out var ts) && float.TryParse(ts, System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : 0.5f;

// 지원 이미지 확장자만 골라 이름순으로 정렬한다(실행마다 같은 순서 -> 재현 가능한 측정).
var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff" };
var files = Directory.EnumerateFiles(folder).Where(f => exts.Contains(Path.GetExtension(f))).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
if (files.Length == 0) { Console.Error.WriteLine("이미지가 없습니다."); return 2; }

// 앱과 같은 구성 요소를 만든다. ROI 는 지정하지 않아 이미지 전체를 쓴다.
var settings = new InspectionSettings { Threshold = threshold };
var pre = new OpenCvPreprocessor();
using var engine = new OnnxClassifier(modelPath);
var judge = new ThresholdJudge();
var proc = Process.GetCurrentProcess();

// 작은 도우미 함수: Stopwatch 틱 -> 밀리초, 소수 1자리 문자열, 바이트 -> MB(1048576 = 1024*1024).
static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
static string F(double v) => v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
static double Mb(long bytes) => bytes / 1048576.0;

// ── 첫 호출(JIT·ONNX 초기화 포함) ──
// 첫 호출은 JIT 컴파일·ONNX 세션 초기화 때문에 유독 느리므로 따로 재고, 이어서 몇 장 더 돌려 워밍업한다(본 측정에서 제외).
var sw = Stopwatch.StartNew();
var first = judge.Decide(engine.Run(pre.Process(new ImageFrame(files[0]), settings)), settings);
double firstCallMs = sw.Elapsed.TotalMilliseconds;
for (int i = 1; i < Math.Min(5, files.Length); i++)   // 나머지 워밍업(측정 제외)
    judge.Decide(engine.Run(pre.Process(new ImageFrame(files[i]), settings)), settings);

// ── A. 단계별 시간(순차 실행) + 반복마다 메모리 ──
// 단계별 소요 시간을 마이크로초(us) 정수로 모은다(ms 로는 작은 값이 뭉개지기 때문). memRows 는 반복마다의 메모리 스냅샷.
var preUs = new List<int>(); var inferUs = new List<int>(); var judgeUs = new List<int>(); var totalUs = new List<int>();
var memRows = new List<(int rep, double managed, double priv, double ws)>();
int ok = 0, ng = 0, err = 0;
// 전체 벽시계 시간(처리량 = 장수 / 시간 계산용).
var wall = Stopwatch.StartNew();
for (int rep = 1; rep <= repeat; rep++)
{
    foreach (var f in files)
    {
        // 같은 이미지를 단계마다 시각을 찍어 전처리(t0~t1) / 추론(t1~t2) / 판정(t2~t3) 구간으로 나눈다.
        long t0 = Stopwatch.GetTimestamp();
        var img = pre.Process(new ImageFrame(f), settings);
        long t1 = Stopwatch.GetTimestamp();
        var o = engine.Run(img);
        long t2 = Stopwatch.GetTimestamp();
        var j = judge.Decide(o, settings);
        long t3 = Stopwatch.GetTimestamp();
        preUs.Add((int)(Ms(t1 - t0) * 1000)); inferUs.Add((int)(Ms(t2 - t1) * 1000));
        judgeUs.Add((int)(Ms(t3 - t2) * 1000)); totalUs.Add((int)(Ms(t3 - t0) * 1000));
        // 판정 건수는 1회차만 센다(같은 폴더를 반복하므로 매 회 같은 결과).
        if (rep == 1) { if (j.Verdict == Verdict.OK) ok++; else if (j.Verdict == Verdict.NG) ng++; else err++; }
    }
    // 메모리 측정 전에 가비지 컬렉션을 강제해 "쓰레기가 아직 안 치워진" 값이 섞이지 않게 한다.
    // 반복해도 프라이빗 바이트가 계속 늘면 네이티브(OpenCV/ONNX) 자원 누수를 의심할 수 있다.
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    proc.Refresh();
    memRows.Add((rep, Mb(GC.GetTotalMemory(true)), Mb(proc.PrivateMemorySize64), Mb(proc.WorkingSet64)));
}
double wallSec = wall.Elapsed.TotalSeconds;
int n = totalUs.Count;

// 통계 문자열(평균·p50·p95·최대, ms). p95 = 95% 지점 값으로 "가끔 느린 장"을 보여 준다. 백분위는 앱 통계와 같은 StatsCalculator 를 재사용.
string Stat(List<int> us)
{
    var s = us.OrderBy(x => x).ToArray();
    return $"평균 {F(s.Average() / 1000)} · p50 {F(StatsCalculator.Percentile(s, 0.50) / 1000.0)} · p95 {F(StatsCalculator.Percentile(s, 0.95) / 1000.0)} · 최대 {F(s[^1] / 1000.0)}";
}

// ── B. 파이프라인 처리량(워커 수별, 저장소 없이) ──
// 워커(병렬 소비자) 수를 1/2/4로 바꿔 가며 실제 InspectionPipeline 의 처리량을 잰다. 저장소는 아무것도 안 하는 NullRepository.
var pipelineRows = new List<(int workers, double sec, double perSec)>();
foreach (var workers in new[] { 1, 2, 4 })
{
    var pipe = new InspectionPipeline(pre, engine, judge, new NullRepository());
    var cfg = settings with { Workers = workers, QueueCapacity = 8 };
    var w = Stopwatch.StartNew();
    var sum = await pipe.RunAsync(new ListSource(Enumerable.Repeat(files, 3).SelectMany(x => x).ToArray()), cfg, null, CancellationToken.None);
    w.Stop();
    pipelineRows.Add((workers, w.Elapsed.TotalSeconds, sum.Total / w.Elapsed.TotalSeconds));
}

// ── 보고서 ──
// 측정 결과를 Markdown 으로 조립한다. "이 PC·이 모델·이 이미지에서만 유효"라는 한계를 보고서에도 명시한다.
var sb = new StringBuilder();
sb.AppendLine("# VisionQC 성능·메모리 측정 결과");
sb.AppendLine();
sb.AppendLine($"- 측정 시각: {DateTime.Now:yyyy-MM-dd HH:mm}");
sb.AppendLine($"- 환경: {(a.TryGetValue("cpu", out var cpu) ? cpu + ", " : "")}논리 코어 {Environment.ProcessorCount}개, {RuntimeInformation.OSDescription}, .NET {Environment.Version}, ONNX Runtime CPU");
sb.AppendLine($"- 모델: {engine.Model.Name} (버전 {engine.Model.Version}), 입력 {settings.InputSize}px, 임계값 {threshold:F3}");
sb.AppendLine($"- 이미지: 폴더 {files.Length}장 × {repeat}회 반복 = {n}장 (첫 호출과 워밍업 5장 제외, 모두 같은 폴더를 반복 사용)");
sb.AppendLine($"- 1회차 판정: OK {ok} / NG {ng} / ERROR {err}");
sb.AppendLine();
sb.AppendLine("## A. 장당 처리시간 (순차 실행, ms)");
sb.AppendLine();
sb.AppendLine("| 구간 | 통계 |");
sb.AppendLine("|---|---|");
sb.AppendLine($"| 전처리(디코드·리사이즈·정규화) | {Stat(preUs)} |");
sb.AppendLine($"| 추론(ONNX) | {Stat(inferUs)} |");
sb.AppendLine($"| 판정 | {Stat(judgeUs)} |");
sb.AppendLine($"| **합계(전처리+추론+판정)** | **{Stat(totalUs)}** |");
sb.AppendLine();
sb.AppendLine($"- 첫 호출(JIT·세션 초기화 포함): {F(firstCallMs)} ms");
sb.AppendLine($"- 순차 처리량: {n / wallSec:F1}장/초 ({n}장, {wallSec:F1}초)");
sb.AppendLine($"- 합계가 500ms를 넘은 장수: {totalUs.Count(x => x > 500_000)} / {n}");
sb.AppendLine();
sb.AppendLine("## B. 파이프라인 처리량 (저장소 없음, 큐 용량 8, 같은 폴더 3회)");
sb.AppendLine();
sb.AppendLine("| 워커 수 | 소요(초) | 장/초 |");
sb.AppendLine("|---|---|---|");
foreach (var p in pipelineRows) sb.AppendLine($"| {p.workers} | {p.sec:F1} | {p.perSec:F1} |");
sb.AppendLine();
sb.AppendLine("## C. 메모리 (반복마다 GC 후 측정, MB)");
sb.AppendLine();
sb.AppendLine("| 반복 | 관리 힙 | 프라이빗 바이트 | 워킹셋 |");
sb.AppendLine("|---|---|---|---|");
foreach (var m in memRows) sb.AppendLine($"| {m.rep} | {m.managed:F1} | {m.priv:F1} | {m.ws:F1} |");
var firstRep = memRows[0]; var lastRep = memRows[^1];
// 1,000장당 프라이빗 바이트 증가량 = (마지막 - 처음 변화량) / 처리한 장수 * 1000. 누수 추세를 보는 지표.
double perThousand = (lastRep.priv - firstRep.priv) / ((repeat - 1) * (double)files.Length) * 1000;
sb.AppendLine();
sb.AppendLine($"- 1회차→{repeat}회차 프라이빗 바이트 변화: {lastRep.priv - firstRep.priv:+0.0;-0.0;0.0} MB ({(repeat - 1) * files.Length}장 처리 동안, 1,000장당 약 {perThousand:+0.00;-0.00;0.00} MB)");
sb.AppendLine($"- 1회차→{repeat}회차 관리 힙 변화: {lastRep.managed - firstRep.managed:+0.0;-0.0;0.0} MB");
sb.AppendLine();
sb.AppendLine("> 해석: 반복해도 프라이빗 바이트가 계속 늘면 네이티브(OpenCV `Mat`·ONNX 텐서) 누수를 의심한다. 이 표는 단기 측정이며 수 시간~수일 운전의 안정성을 보장하지 않는다.");

// 콘솔에 출력하고, --out 이 있으면 BOM 없는 UTF-8 로 파일에도 저장한다. 종료 코드 0 = 성공.
Console.WriteLine(sb.ToString());
if (a.TryGetValue("out", out var outPath)) File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
return 0;

// "--이름 값" 형태 인자를 사전으로 만든다. 값이 없는 옵션(바로 다음이 또 "--")은 빈 문자열. 키는 대소문자 무시.
static Dictionary<string, string> ParseArgs(string[] argv)
{
    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < argv.Length; i++)
    {
        if (!argv[i].StartsWith("--")) continue;
        d[argv[i][2..]] = i + 1 < argv.Length && !argv[i + 1].StartsWith("--") ? argv[++i] : "";
    }
    return d;
}

/// <summary>
/// 경로 배열을 이미지 소스(IImageSource)로 감싼 벤치마크용 공급원. 같은 폴더를 3번 반복한 목록을 파이프라인에 흘려 보내는 데 쓴다.
/// </summary>
internal sealed class ListSource : IImageSource
{
    private readonly string[] _paths;
    public ListSource(string[] paths) => _paths = paths;
    public string Name => "bench";
    // 취소 요청을 존중하며 경로를 하나씩 내보낸다. Task.Yield 로 매번 제어를 넘겨 실제 비동기 공급원처럼 동작시킨다.
    public async IAsyncEnumerable<ImageFrame> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var p in _paths) { ct.ThrowIfCancellationRequested(); yield return new ImageFrame(p); await Task.Yield(); }
    }
}

/// <summary>
/// 아무것도 저장하지 않는 결과 저장소(IResultRepository). DB 쓰기 시간이 측정에 섞이지 않게 하려는 벤치마크 전용 가짜 구현이다.
/// 조회 계열은 빈 결과를 돌려준다.
/// </summary>
internal sealed class NullRepository : IResultRepository
{
    public Task<long> BeginJobAsync(ModelInfo model, string sourceName, float threshold, CancellationToken ct = default) => Task.FromResult(1L);
    public Task EndJobAsync(long jobId, JobStatus status, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveAsync(InspectionResult result, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<InspectionResult>> QueryAsync(HistoryFilter filter, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<InspectionResult>>(Array.Empty<InspectionResult>());
    public Task<StatsData> QueryStatsAsync(StatsFilter filter, CancellationToken ct = default) => Task.FromResult(new StatsData(Array.Empty<StatRow>(), Array.Empty<DefectCount>()));
    public Task AddReviewAsync(Review review, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<ReviewedResult>> QueryReviewedAsync(HistoryFilter filter, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ReviewedResult>>(Array.Empty<ReviewedResult>());
    public Task<IReadOnlyList<Review>> GetReviewsAsync(long resultId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Review>>(Array.Empty<Review>());
}
