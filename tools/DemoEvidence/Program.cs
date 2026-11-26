using System.Text;
using System.Text.Json;
using VisionQC.Core;
using VisionQC.Data;
using VisionQC.Imaging;
using VisionQC.Inference;

// 로컬 시연 DB만 만든다. 사용자의 운영 DB나 계정을 사용하지 않는다.
if (args.Length != 1) throw new ArgumentException("사용법: dotnet run --project tools/DemoEvidence -- <프로젝트 루트>");
var root = Path.GetFullPath(args[0]);
var assets = Path.Combine(root, "demo-assets");
var runtime = Path.Combine(root, "demo-runtime");
Directory.CreateDirectory(runtime);
var db = Path.Combine(runtime, "demo.db");
if (File.Exists(db)) throw new IOException("시연 DB가 이미 있습니다. 보존을 위해 중단합니다: " + db);
var repo = new SqliteResultRepository(db);
var accounts = new SqliteAccountRepository(db);
var audit = new AuditLogger(accounts);
var auth = new AuthService(accounts, audit);
await auth.CreateFirstAdminAsync("portfolio", "Vision!2026*");
var modelPath = Path.Combine(assets, "model_cam.onnx");
var threshold = 0.67794269323349f;
new SettingsStore(Path.Combine(runtime, "settings.json")).Save(new AppSettings
{
    ModelPath = modelPath, FolderPath = Path.Combine(assets, "images"), Threshold = threshold, AutoBackupHours = 0
});
using var engine = new OnnxClassifier(modelPath);
var pipeline = new InspectionPipeline(new OpenCvPreprocessor(), engine, new ThresholdJudge(), repo);
var summary = await pipeline.RunAsync(new FolderImageSource(Path.Combine(assets, "images")), new InspectionSettings { Threshold = threshold }, null, CancellationToken.None);
if (summary.Total != 50 || summary.Error != 0 || summary.SaveFailures != 0) throw new Exception("시험셋 50장 처리 실패");
var rows = await repo.QueryAsync(new HistoryFilter(Limit: 500));
using var splitDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(assets, "split.json")));
var truth = splitDoc.RootElement.GetProperty("test").EnumerateArray().ToDictionary(
    x => Path.GetFileName(x[0].GetString()!), x => x[1].GetInt32());
int tp=0, fn=0, fp=0, tn=0;
foreach (var row in rows)
{
    var positive = truth[Path.GetFileName(row.ImagePath)] == 1;
    if (row.Verdict == Verdict.NG) { if (positive) tp++; else fp++; }
    else { if (positive) fn++; else tn++; }
}
using (var writer = new StreamWriter(Path.Combine(runtime, "history-original.csv"), false, new UTF8Encoding(true))) HistoryCsv.Write(writer, rows);
var badFolder = Path.Combine(runtime, "error-fixture");
Directory.CreateDirectory(badFolder);
File.WriteAllText(Path.Combine(badFolder, "broken.png"), "intentional corrupt image fixture");
var errorSummary = await pipeline.RunAsync(new FolderImageSource(badFolder), new InspectionSettings { Threshold = threshold }, null, CancellationToken.None);
if (errorSummary.Error != 1 || errorSummary.Ok != 0) throw new Exception("손상 이미지 ERROR 처리 실패");
var ng = rows.First(r => r.Verdict == Verdict.NG);
await repo.AddReviewAsync(new Review(0, ng.Id, "portfolio", Verdict.NG, "시연 준비: 원 판정 확인 (정답 추가 실험 아님)", DateTime.UtcNow));
var metrics = new { date = "2026-11-17", model = engine.Model, threshold, testImages = 50, tp, fn, fp, tn,
    recall = (double)tp/(tp+fn), precision = (double)tp/(tp+fp), summary,
    errorFixture = errorSummary, heatmapsStored = rows.Count(x => x.Heatmap is not null),
    elapsed = StatsCalculator.Build(await repo.QueryStatsAsync(new StatsFilter()), TimeZoneInfo.Local).Time,
    note = "기존 고정 시험셋 재검증. 신규 외부 시험 아님. ERROR fixture는 정확도 산정에서 제외." };
File.WriteAllText(Path.Combine(runtime, "validation.json"), JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(metrics));
Console.WriteLine("시연 계정: portfolio (비밀번호는 tools/DemoEvidence/Program.cs의 로컬 시연 전용 값)");
