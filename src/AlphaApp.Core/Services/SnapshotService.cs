using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AlphaApp.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlphaApp.Core.Services;

public class SnapshotService : ISnapshotService
{
    private readonly IQemuManager _qemu;
    private readonly IImageManager _images;
    private readonly QemuOptions _options;
    private readonly ILogger<SnapshotService> _logger;
    private readonly string _metadataDir;

    public SnapshotService(
        IQemuManager qemu,
        IImageManager images,
        IOptions<QemuOptions> options,
        ILogger<SnapshotService> logger)
    {
        _qemu = qemu;
        _images = images;
        _options = options.Value;
        _logger = logger;
        _metadataDir = Path.Combine(_options.SnapshotsDirectory, ".metadata");
        Directory.CreateDirectory(_metadataDir);
    }

    public async Task<PipelineResult> CreateSnapshotAsync(AlphaAppDefinition app, CancellationToken ct = default)
        => await CreateSnapshotAsync(app, null, ct);

    public async Task<PipelineResult> CreateSnapshotAsync(AlphaAppDefinition app, IProgress<BuildProgressEvent>? progress, CancellationToken ct = default)
    {
        var result = new PipelineResult();
        var totalSw = Stopwatch.StartNew();
        GuestState? guest = null;
        const int totalSteps = 7;

        _logger.LogInformation("🚀 ═══════════════════════════════════════");
        _logger.LogInformation("🚀 AlphaApp Pipeline: {Name}", app.Name);
        _logger.LogInformation("🚀 ═══════════════════════════════════════");

        try
        {
            // ── الخطوة 1: بناء صورة القرص ──
            ReportProgress(progress, 1, totalSteps, "بناء صورة القرص", "running", "snapshot");
            var step1 = await ExecuteStepAsync("بناء صورة القرص", async () =>
            {
                app.Status = AlphaAppStatus.Building;
                var diskPath = await _images.BuildAppImageAsync(app, ct);
                return diskPath;
            });
            result.Steps.Add(step1.step);
            if (!step1.step.Success) { ReportProgress(progress, 1, totalSteps, "بناء صورة القرص", "error", "snapshot", step1.step.Message); return Fail(result, totalSw, "فشل بناء الصورة"); }
            ReportProgress(progress, 1, totalSteps, "بناء صورة القرص", "done", "snapshot", duration: step1.step.Duration.TotalSeconds);
            var diskImagePath = (string)step1.value!;

            // ── الخطوة 2: تحميل صورة التوزيعة ──
            ReportProgress(progress, 2, totalSteps, "تحميل صورة التوزيعة", "running", "snapshot");
            var step2 = await ExecuteStepAsync("تحميل صورة التوزيعة", async () =>
            {
                return await _images.DownloadBaseImageAsync(app.BaseDistro, app.Architecture, ct);
            });
            result.Steps.Add(step2.step);
            if (!step2.step.Success) { ReportProgress(progress, 2, totalSteps, "تحميل صورة التوزيعة", "error", "snapshot", step2.step.Message); return Fail(result, totalSw, "فشل تحميل التوزيعة"); }
            ReportProgress(progress, 2, totalSteps, "تحميل صورة التوزيعة", "done", "snapshot", duration: step2.step.Duration.TotalSeconds);

            // ── الخطوة 3: إقلاع QEMU ──
            ReportProgress(progress, 3, totalSteps, "إقلاع الضيف", "running", "snapshot");
            var step3 = await ExecuteStepAsync("إقلاع الضيف", async () =>
            {
                app.Status = AlphaAppStatus.Booting;
                guest = await _qemu.BootGuestAsync(app, diskImagePath, ct);
                return guest;
            });
            result.Steps.Add(step3.step);
            if (!step3.step.Success) { ReportProgress(progress, 3, totalSteps, "إقلاع الضيف", "error", "snapshot", step3.step.Message); return Fail(result, totalSw, "فشل الإقلاع"); }
            ReportProgress(progress, 3, totalSteps, "إقلاع الضيف", "done", "snapshot", duration: step3.step.Duration.TotalSeconds);

            // ── الخطوة 4: انتظار جاهزية التطبيق ──
            ReportProgress(progress, 4, totalSteps, "انتظار جاهزية التطبيق", "running", "snapshot");
            var step4 = await ExecuteStepAsync("انتظار جاهزية التطبيق", async () =>
            {
                app.Status = AlphaAppStatus.Running;
                var ready = await _qemu.WaitForGuestReadyAsync(guest!, _options.AppReadyTimeoutSeconds, ct);
                if (!ready)
                    throw new TimeoutException("التطبيق لم يصبح جاهزاً خلال المهلة المحددة");
                return ready;
            });
            result.Steps.Add(step4.step);
            if (!step4.step.Success) { ReportProgress(progress, 4, totalSteps, "انتظار جاهزية التطبيق", "error", "snapshot", step4.step.Message); return Fail(result, totalSw, "التطبيق لم يصبح جاهزاً"); }
            ReportProgress(progress, 4, totalSteps, "انتظار جاهزية التطبيق", "done", "snapshot", duration: step4.step.Duration.TotalSeconds);

            // ── الخطوة 5: تجميد الضيف ──
            ReportProgress(progress, 5, totalSteps, "تجميد الضيف", "running", "snapshot");
            var step5 = await ExecuteStepAsync("تجميد الضيف", async () =>
            {
                app.Status = AlphaAppStatus.Freezing;
                var frozen = await _qemu.FreezeGuestAsync(guest!, ct);
                if (!frozen)
                    throw new InvalidOperationException("فشل تجميد الضيف");
                return frozen;
            });
            result.Steps.Add(step5.step);
            if (!step5.step.Success) { ReportProgress(progress, 5, totalSteps, "تجميد الضيف", "error", "snapshot", step5.step.Message); return Fail(result, totalSw, "فشل التجميد"); }
            ReportProgress(progress, 5, totalSteps, "تجميد الضيف", "done", "snapshot", duration: step5.step.Duration.TotalSeconds);

            // ── الخطوة 6: حفظ حالة VM ──
            ReportProgress(progress, 6, totalSteps, "حفظ حالة VM (savevm)", "running", "snapshot");
            var snapshot = new SnapshotInfo
            {
                AppId = app.Id,
                AppName = app.Name,
                DiskImagePath = diskImagePath,
                Architecture = app.Architecture,
                MemoryMB = app.MemoryMB,
                GuestPort = app.GuestPort,
                Status = SnapshotStatus.Creating,
                TotalSizeBytes = new FileInfo(diskImagePath).Length,
            };
            var snapshotName = $"alpha-{snapshot.Id}";
            snapshot.SnapshotName = snapshotName;
            var step6 = await ExecuteStepAsync("حفظ حالة VM (savevm)", async () =>
            {
                app.Status = AlphaAppStatus.Frozen;
                return await _qemu.SaveVmStateAsync(guest!, snapshotName, ct);
            });
            result.Steps.Add(step6.step);
            if (!step6.step.Success) { ReportProgress(progress, 6, totalSteps, "حفظ حالة VM (savevm)", "error", "snapshot", step6.step.Message); return Fail(result, totalSw, "فشل حفظ الحالة"); }
            ReportProgress(progress, 6, totalSteps, "حفظ حالة VM (savevm)", "done", "snapshot", duration: step6.step.Duration.TotalSeconds);

            // ── الخطوة 7: إنشاء بيانات اللقطة ──
            ReportProgress(progress, 7, totalSteps, "إنشاء بيانات اللقطة", "running", "snapshot");
            snapshot.Status = SnapshotStatus.Ready;
            snapshot.TotalSizeBytes = new FileInfo(diskImagePath).Length;
            snapshot.Checksum = await ComputeChecksumAsync(diskImagePath, ct);

            // حفظ metadata
            await SaveSnapshotMetadataAsync(snapshot, ct);
            ReportProgress(progress, 7, totalSteps, "إنشاء بيانات اللقطة", "done", "snapshot");

            // ── إيقاف الضيف ──
            await _qemu.StopGuestAsync(guest, ct);

            app.Status = AlphaAppStatus.Exported;
            result.Success = true;
            result.Message = $"✅ لقطة {app.Name} جاهزة — {snapshot.TotalSizeBytes / 1048576.0:F1}MB";
            result.Snapshot = snapshot;
            result.Duration = totalSw.Elapsed;

            _logger.LogInformation("🎉 ═══════════════════════════════════════");
            _logger.LogInformation("🎉 Pipeline مكتمل: {Name} في {Duration:F1}s", app.Name, totalSw.Elapsed.TotalSeconds);
            _logger.LogInformation("🎉 ═══════════════════════════════════════");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "💥 فشل Pipeline لـ {Name}", app.Name);
            app.Status = AlphaAppStatus.Failed;
            result.Error = ex.Message;

            if (guest != null)
            {
                try { await _qemu.StopGuestAsync(guest, CancellationToken.None); }
                catch { /* تنظيف */ }
            }
        }

        return result;
    }

    public async Task<SnapshotInfo?> GetSnapshotAsync(string snapshotId, CancellationToken ct = default)
    {
        var path = Path.Combine(_metadataDir, $"{snapshotId}.json");
        if (!File.Exists(path)) return null;
        var json = await File.ReadAllTextAsync(path, ct);
        return JsonSerializer.Deserialize<SnapshotInfo>(json);
    }

    public async Task<List<SnapshotInfo>> ListSnapshotsAsync(CancellationToken ct = default)
    {
        var snapshots = new List<SnapshotInfo>();
        if (!Directory.Exists(_metadataDir)) return snapshots;

        foreach (var file in Directory.GetFiles(_metadataDir, "*.json"))
        {
            var json = await File.ReadAllTextAsync(file, ct);
            var snapshot = JsonSerializer.Deserialize<SnapshotInfo>(json);
            if (snapshot != null) snapshots.Add(snapshot);
        }
        return snapshots.OrderByDescending(s => s.CreatedAt).ToList();
    }

    public async Task<bool> DeleteSnapshotAsync(string snapshotId, CancellationToken ct = default)
    {
        var snapshot = await GetSnapshotAsync(snapshotId, ct);
        if (snapshot == null) return false;

        if (File.Exists(snapshot.DiskImagePath))
            File.Delete(snapshot.DiskImagePath);
        if (File.Exists(snapshot.MemoryStatePath))
            File.Delete(snapshot.MemoryStatePath);

        var metaPath = Path.Combine(_metadataDir, $"{snapshotId}.json");
        if (File.Exists(metaPath)) File.Delete(metaPath);

        return true;
    }

    private async Task SaveSnapshotMetadataAsync(SnapshotInfo snapshot, CancellationToken ct)
    {
        var path = Path.Combine(_metadataDir, $"{snapshot.Id}.json");
        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json, ct);
    }

    private async Task<(PipelineStep step, object? value)> ExecuteStepAsync(string name, Func<Task<object>> action)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogInformation("  ── {Step}...", name);

        try
        {
            var value = await action();
            sw.Stop();
            _logger.LogInformation("  ✅ {Step} ({Duration:F1}s)", name, sw.Elapsed.TotalSeconds);
            return (new PipelineStep { Name = name, Success = true, Message = "مكتمل", Duration = sw.Elapsed }, value);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError("  ❌ {Step}: {Error}", name, ex.Message);
            return (new PipelineStep { Name = name, Success = false, Message = ex.Message, Duration = sw.Elapsed }, null);
        }
    }

    private PipelineResult Fail(PipelineResult result, Stopwatch sw, string message)
    {
        result.Success = false;
        result.Error = message;
        result.Duration = sw.Elapsed;
        return result;
    }

    private async Task<string> ComputeChecksumAsync(string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexStringLower(hash);
    }

    private static void ReportProgress(IProgress<BuildProgressEvent>? progress, int step, int total,
        string name, string status, string phase, string? message = null, double duration = 0)
    {
        progress?.Report(new BuildProgressEvent
        {
            StepNumber = step,
            TotalSteps = total,
            StepName = name,
            Status = status,
            Message = message,
            DurationSeconds = duration,
            OverallPercent = status == "done" ? (step * 100.0 / total) : ((step - 1) * 100.0 / total + 50.0 / total),
            Phase = phase
        });
    }
}
