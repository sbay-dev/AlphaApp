using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AlphaApp.Core.Models;
using AlphaApp.Core.Pipeline;
using AlphaApp.Core.Services;
using AlphaApp.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace AlphaApp.Web.Controllers;

public class AppsController : Controller
{
    private readonly AlphaAppPipeline _pipeline;
    private readonly ISnapshotService _snapshots;
    private readonly IImageManager _images;
    private readonly ProjectAnalyzer _analyzer;
    private readonly ILogger<AppsController> _logger;

    public AppsController(
        AlphaAppPipeline pipeline,
        ISnapshotService snapshots,
        IImageManager images,
        ProjectAnalyzer analyzer,
        ILogger<AppsController> logger)
    {
        _pipeline = pipeline;
        _snapshots = snapshots;
        _images = images;
        _analyzer = analyzer;
        _logger = logger;
    }

    public async Task<IActionResult> Index()
    {
        var snapshots = await _snapshots.ListSnapshotsAsync();
        return View(snapshots);
    }

    [HttpGet]
    public IActionResult Create()
    {
        var vm = new CreateAppViewModel
        {
            BaseDistro = "alpine",
            Architecture = "x86_64",
            GuestPort = 5000,
            MemoryMB = 256
        };
        return View(vm);
    }

    /// <summary>استعراض المجلدات (AJAX)</summary>
    [HttpGet("/api/apps/browse")]
    public IActionResult Browse([FromQuery] string path = "")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                // اكتشاف المجلد الرئيسي بذكاء (proot / Termux / عادي)
                path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrWhiteSpace(path))
                    path = Environment.GetEnvironmentVariable("HOME") ?? "";
                if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                    path = Directory.Exists("/root") ? "/root" : "/";

                // في Termux/proot: إذا /root فارغ تقريباً، جرّب مسار Termux المباشر
                const string termuxHome = "/data/data/com.termux/files/home";
                if (path == "/root" && Directory.Exists(termuxHome))
                {
                    try
                    {
                        var rootCount = Directory.GetDirectories("/root").Count(d => !Path.GetFileName(d).StartsWith('.'));
                        var termuxCount = Directory.GetDirectories(termuxHome).Count(d => !Path.GetFileName(d).StartsWith('.'));
                        if (termuxCount > rootCount)
                            path = termuxHome;
                    }
                    catch { }
                }
            }

            path = Path.GetFullPath(path);

            if (!Directory.Exists(path))
                return BadRequest(new { error = "المسار غير موجود" });

            var entries = new List<object>();
            var parent = Directory.GetParent(path)?.FullName;

            // المجلدات
            foreach (var dir in Directory.GetDirectories(path).OrderBy(d => d))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith('.')) continue; // إخفاء المخفية
                bool hasCsproj = false;
                try { hasCsproj = Directory.GetFiles(dir, "*.csproj").Length > 0; } catch { }
                entries.Add(new
                {
                    name,
                    fullPath = dir,
                    type = "dir",
                    hasCsproj
                });
            }

            // ملفات .csproj في المجلد الحالي
            foreach (var file in Directory.GetFiles(path, "*.csproj").OrderBy(f => f))
            {
                entries.Add(new
                {
                    name = Path.GetFileName(file),
                    fullPath = file,
                    type = "csproj",
                    hasCsproj = false
                });
            }

            bool currentHasCsproj = Directory.GetFiles(path, "*.csproj").Length > 0;

            return Json(new { currentPath = path, parent, entries, currentHasCsproj });
        }
        catch (UnauthorizedAccessException)
        {
            return BadRequest(new { error = "لا توجد صلاحية للوصول" });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>تحليل المشروع تلقائياً عند إدخال المسار (AJAX)</summary>
    [HttpPost("/api/apps/analyze")]
    public async Task<IActionResult> Analyze([FromBody] AnalyzeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ProjectPath))
            return BadRequest(new { error = "المسار مطلوب" });

        if (!Directory.Exists(request.ProjectPath) && !System.IO.File.Exists(request.ProjectPath))
            return BadRequest(new { error = "المسار غير موجود" });

        var analysis = await _analyzer.AnalyzeAsync(request.ProjectPath);
        return Json(analysis);
    }

    /// <summary>نشر المشروع تلقائياً (AJAX)</summary>
    [HttpPost("/api/apps/publish")]
    public async Task<IActionResult> Publish([FromBody] AnalyzeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ProjectPath))
            return BadRequest(new { error = "المسار مطلوب" });

        var result = await _analyzer.PublishAsync(request.ProjectPath);
        return Json(result);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateAppViewModel model)
    {
        // هذه الحقول تُملأ تلقائياً — لا نتحقق منها مسبقاً
        ModelState.Remove(nameof(model.PublishedAppPath));
        ModelState.Remove(nameof(model.EntryCommand));
        ModelState.Remove(nameof(model.Packages));
        ModelState.Remove(nameof(model.Description));
        ModelState.Remove(nameof(model.ProjectSourcePath));

        if (!ModelState.IsValid) return View(model);

        // إذا لم يكن هناك مسار نشر، ننشر تلقائياً
        if (string.IsNullOrWhiteSpace(model.PublishedAppPath) && !string.IsNullOrWhiteSpace(model.ProjectSourcePath))
        {
            _logger.LogInformation("📦 نشر تلقائي من {Path}...", model.ProjectSourcePath);
            var publishResult = await _analyzer.PublishAsync(model.ProjectSourcePath);
            if (!publishResult.Success)
            {
                ModelState.AddModelError("", $"فشل النشر: {publishResult.Error}");
                return View(model);
            }
            model.PublishedAppPath = publishResult.OutputPath!;
        }

        // إذا لم يُحدد أمر التشغيل، نكتشفه تلقائياً
        if (string.IsNullOrWhiteSpace(model.EntryCommand) && !string.IsNullOrWhiteSpace(model.ProjectSourcePath))
        {
            var analysis = await _analyzer.AnalyzeAsync(model.ProjectSourcePath);
            model.EntryCommand = analysis.EntryCommand;
            if (model.GuestPort == 5000) model.GuestPort = analysis.DetectedPort;
        }

        var app = new AlphaAppDefinition
        {
            Name = model.Name,
            Description = model.Description ?? string.Empty,
            BaseDistro = model.BaseDistro,
            Architecture = model.Architecture,
            PublishedAppPath = model.PublishedAppPath ?? string.Empty,
            EntryCommand = model.EntryCommand ?? string.Empty,
            GuestPort = model.GuestPort,
            MemoryMB = model.MemoryMB,
            Packages = string.IsNullOrWhiteSpace(model.Packages)
                ? [] : model.Packages.Split(',', StringSplitOptions.TrimEntries).ToList()
        };

        _logger.LogInformation("🚀 إنشاء AlphaApp: {Name}", app.Name);

        var result = await _pipeline.BuildAlphaAppAsync(app);

        if (result.Success)
        {
            TempData["Success"] = result.Message;
            return RedirectToAction(nameof(Index));
        }

        ModelState.AddModelError("", result.Error ?? "فشل إنشاء التطبيق");
        return View(model);
    }

    public async Task<IActionResult> Details(string id)
    {
        var snapshot = await _snapshots.GetSnapshotAsync(id);
        if (snapshot == null) return NotFound();
        return View(snapshot);
    }

    /// <summary>بناء عبر SSE — بث التقدّم الحي عبر نفس اتصال HTTP (بدون SignalR)</summary>
    [HttpPost("/api/apps/build")]
    public async Task BuildAsync([FromBody] BuildRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            Response.StatusCode = 400;
            await Response.WriteAsync("{\"error\":\"اسم التطبيق مطلوب\"}");
            return;
        }

        // تهيئة SSE
        Response.ContentType = "text/event-stream; charset=utf-8";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["Connection"] = "keep-alive";
        Response.Headers["X-Accel-Buffering"] = "no";
        var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        // Channel: الأحداث تُكتب من أي thread وتُقرأ من thread الطلب
        var channel = Channel.CreateUnbounded<BuildProgressEvent>(
            new UnboundedChannelOptions { SingleReader = true });

        async Task SendEvent(BuildProgressEvent evt)
        {
            var json = JsonSerializer.Serialize(evt, jsonOpts);
            var sseData = $"data: {json}\n\n";
            await Response.Body.WriteAsync(Encoding.UTF8.GetBytes(sseData));
            await Response.Body.FlushAsync();
        }

        // IProgress متزامن — يكتب مباشرة بدون thread pool لتجنّب فقدان الأحداث
        IProgress<BuildProgressEvent> progress = new SyncProgress<BuildProgressEvent>(evt =>
        {
            channel.Writer.TryWrite(evt);
        });

        // تشغيل Pipeline في الخلفية — الأحداث تصل عبر Channel
        var buildTask = Task.Run(async () =>
        {
            try
            {
                // نشر تلقائي إذا لزم
                string? publishedPath = request.PublishedAppPath;
                if (string.IsNullOrWhiteSpace(publishedPath) && !string.IsNullOrWhiteSpace(request.ProjectSourcePath))
                {
                    channel.Writer.TryWrite(new BuildProgressEvent
                    {
                        StepNumber = 0, TotalSteps = 9, StepName = "نشر المشروع (dotnet publish)",
                        Status = "running", OverallPercent = 0, Phase = "publish"
                    });

                    var publishResult = await _analyzer.PublishAsync(request.ProjectSourcePath);
                    if (!publishResult.Success)
                    {
                        channel.Writer.TryWrite(new BuildProgressEvent
                        {
                            StepNumber = 0, TotalSteps = 9, StepName = "نشر المشروع",
                            Status = "error", Message = publishResult.Error, OverallPercent = 0, Phase = "publish"
                        });
                        return;
                    }
                    publishedPath = publishResult.OutputPath;
                    channel.Writer.TryWrite(new BuildProgressEvent
                    {
                        StepNumber = 0, TotalSteps = 9, StepName = "نشر المشروع (dotnet publish)",
                        Status = "done", OverallPercent = 5, Phase = "publish",
                        Message = $"تم النشر في {publishedPath}"
                    });
                }

                // اكتشاف أمر التشغيل
                string? entryCommand = request.EntryCommand;
                int guestPort = request.GuestPort > 0 ? request.GuestPort : 5000;
                if (string.IsNullOrWhiteSpace(entryCommand) && !string.IsNullOrWhiteSpace(request.ProjectSourcePath))
                {
                    var analysis = await _analyzer.AnalyzeAsync(request.ProjectSourcePath);
                    entryCommand = analysis.EntryCommand;
                    if (guestPort == 5000) guestPort = analysis.DetectedPort;
                }

                var app = new AlphaAppDefinition
                {
                    Name = request.Name,
                    Description = request.Description ?? string.Empty,
                    BaseDistro = request.BaseDistro ?? "alpine",
                    Architecture = request.Architecture ?? "x86_64",
                    PublishedAppPath = publishedPath ?? string.Empty,
                    EntryCommand = entryCommand ?? string.Empty,
                    GuestPort = guestPort,
                    MemoryMB = request.MemoryMB > 0 ? request.MemoryMB : 256,
                    Packages = string.IsNullOrWhiteSpace(request.Packages)
                        ? [] : request.Packages.Split(',', StringSplitOptions.TrimEntries).ToList()
                };

                _logger.LogInformation("🚀 بناء AlphaApp (SSE): {Name}", app.Name);
                var result = await _pipeline.BuildAlphaAppAsync(app, progress);

                if (!result.Success)
                {
                    channel.Writer.TryWrite(new BuildProgressEvent
                    {
                        StepNumber = 0, TotalSteps = 9, StepName = "خطأ في البناء",
                        Status = "error", Message = result.Error, OverallPercent = 0, Phase = "error"
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "💥 خطأ أثناء البناء");
                channel.Writer.TryWrite(new BuildProgressEvent
                {
                    StepNumber = 0, TotalSteps = 9, StepName = "خطأ غير متوقع",
                    Status = "error", Message = ex.Message, OverallPercent = 0, Phase = "error"
                });
            }
            finally
            {
                channel.Writer.Complete();
            }
        });

        // القراءة من Channel وإرسال SSE على thread الطلب
        await foreach (var evt in channel.Reader.ReadAllAsync())
        {
            await SendEvent(evt);
        }

        await buildTask;
    }

    [HttpPost]
    public async Task<IActionResult> Delete(string id)
    {
        await _snapshots.DeleteSnapshotAsync(id);
        TempData["Success"] = "تم حذف اللقطة";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("/api/apps/{id}/download-cph")]
    public async Task<IActionResult> DownloadCph(string id)
    {
        var snapshot = await _snapshots.GetSnapshotAsync(id);
        if (snapshot == null)
            return NotFound(new { error = "اللقطة غير موجودة" });

        // البحث عن ملف .cph في مجلد wasm
        var wasmDir = Path.Combine(
            Path.GetDirectoryName(snapshot.DiskImagePath) ?? "",
            "..", "..", "snapshots", "wasm", snapshot.Id);
        var cphFiles = Directory.Exists(wasmDir)
            ? Directory.GetFiles(wasmDir, "*.cph")
            : [];

        if (cphFiles.Length == 0)
            return NotFound(new { error = "حزمة .cph غير موجودة — أعد البناء" });

        var cphPath = cphFiles[0];
        var fileName = Path.GetFileName(cphPath);
        var stream = System.IO.File.OpenRead(cphPath);
        return File(stream, "application/x-cepha-app", fileName);
    }
}

public class AnalyzeRequest
{
    public string ProjectPath { get; set; } = string.Empty;
}

public class BuildRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? BaseDistro { get; set; }
    public string? Architecture { get; set; }
    public string? ProjectSourcePath { get; set; }
    public string? PublishedAppPath { get; set; }
    public string? EntryCommand { get; set; }
    public int GuestPort { get; set; }
    public int MemoryMB { get; set; }
    public string? Packages { get; set; }
}

/// <summary>IProgress متزامن — ينفّذ الـ callback مباشرة على thread المُستدعي</summary>
sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;
    public SyncProgress(Action<T> handler) => _handler = handler;
    public void Report(T value) => _handler(value);
}
