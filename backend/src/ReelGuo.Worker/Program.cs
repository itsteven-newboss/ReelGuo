using ReelGuo.Data;
using ReelGuo.Data.Catalog;
using ReelGuo.Data.Jobs;
using ReelGuo.Data.Storage;
using ReelGuo.Data.Telemetry;
using ReelGuo.Worker;
using ReelGuo.Worker.Media;

var builder = Host.CreateApplicationBuilder(args);
// One JSON object per log line outside development, for log collectors.
if (!builder.Environment.IsDevelopment()) builder.Logging.AddJsonConsole(o => { o.UseUtcTimestamp = true; o.IncludeScopes = true; });
builder.Services.AddReelGuoTelemetry(builder.Configuration, "reelguo-worker");

builder.Services.AddDbContext<ReelGuoDbContext>(o =>
{
    var cs = builder.Configuration.GetConnectionString("Default");
    if (string.IsNullOrWhiteSpace(cs)) throw new InvalidOperationException("ConnectionStrings:Default is not set.");
    ReelGuoDbContext.Configure(o, cs);
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddReelGuoStorage(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.AddScoped<MediaJobQueue>();
builder.Services.AddScoped<RankingCalculator>();

var ffmpeg = builder.Configuration.GetSection(FfmpegOptions.Section).Get<FfmpegOptions>() ?? new FfmpegOptions();
if (!string.IsNullOrWhiteSpace(ffmpeg.BinDirectory) && !Path.IsPathRooted(ffmpeg.BinDirectory))
    ffmpeg.BinDirectory = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, ffmpeg.BinDirectory));
if (!string.IsNullOrWhiteSpace(ffmpeg.BinDirectory) && !Directory.Exists(ffmpeg.BinDirectory))
    ffmpeg.BinDirectory = ""; // configured folder missing: use ffmpeg from PATH
builder.Services.AddSingleton(ffmpeg);
builder.Services.AddSingleton(builder.Configuration.GetSection(WorkerOptions.Section).Get<WorkerOptions>() ?? new WorkerOptions());
builder.Services.AddSingleton<Transcoder>();
builder.Services.AddSingleton<WorkerStats>();
builder.Services.AddHostedService<JobRunner>();
builder.Services.AddHostedService<HeartbeatService>();
builder.Services.AddHostedService<RankingsService>();

builder.Build().Run();
