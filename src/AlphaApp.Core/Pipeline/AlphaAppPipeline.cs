using AlphaApp.Core.Models;
using AlphaApp.Core.Services;
using Microsoft.Extensions.Logging;

namespace AlphaApp.Core.Pipeline;

/// <summary>
/// المنسّق الرئيسي: ينفّذ Pipeline كامل من تعريف التطبيق حتى حزمة wasm
/// </summary>
public class AlphaAppPipeline
{
    private readonly ISnapshotService _snapshots;
    private readonly WasmBridgeService _wasmBridge;
    private readonly ILogger<AlphaAppPipeline> _logger;

    public AlphaAppPipeline(
        ISnapshotService snapshots,
        WasmBridgeService wasmBridge,
        ILogger<AlphaAppPipeline> logger)
    {
        _snapshots = snapshots;
        _wasmBridge = wasmBridge;
        _logger = logger;
    }

    /// <summary>
    /// Pipeline كامل: تعريف التطبيق → صورة قرص → إقلاع → تجميد → snapshot → wasm package
    /// </summary>
    public async Task<AlphaAppResult> BuildAlphaAppAsync(AlphaAppDefinition app, CancellationToken ct = default)
        => await BuildAlphaAppAsync(app, null, ct);

    public async Task<AlphaAppResult> BuildAlphaAppAsync(AlphaAppDefinition app, IProgress<BuildProgressEvent>? progress, CancellationToken ct = default)
    {
        _logger.LogInformation("╔══════════════════════════════════════╗");
        _logger.LogInformation("║   AlphaApp Pipeline: {Name,-16} ║", app.Name);
        _logger.LogInformation("╚══════════════════════════════════════╝");

        var result = new AlphaAppResult { App = app };

        // الخطوة 1: إنشاء اللقطة (boot → freeze → savevm)
        _logger.LogInformation("📦 المرحلة 1/2: إنشاء اللقطة...");
        result.PipelineResult = await _snapshots.CreateSnapshotAsync(app, progress, ct);

        if (!result.PipelineResult.Success || result.PipelineResult.Snapshot == null)
        {
            result.Success = false;
            result.Error = result.PipelineResult.Error ?? "فشل إنشاء اللقطة";
            _logger.LogError("❌ فشل Pipeline: {Error}", result.Error);
            return result;
        }

        // الخطوة 2: تحويل لـ wasm
        _logger.LogInformation("🌐 المرحلة 2/2: تجهيز حزمة qemu-wasm...");
        progress?.Report(new BuildProgressEvent
        {
            StepNumber = 8, TotalSteps = 9, StepName = "تجهيز حزمة qemu-wasm",
            Status = "running", OverallPercent = 80, Phase = "wasm"
        });

        result.WasmPackage = await _wasmBridge.PrepareForWasmAsync(result.PipelineResult.Snapshot, ct);

        // تصدير حزمة .cph
        await _wasmBridge.ExportCphAsync(result.PipelineResult.Snapshot, result.WasmPackage, ct);

        progress?.Report(new BuildProgressEvent
        {
            StepNumber = 8, TotalSteps = 9, StepName = "تجهيز حزمة qemu-wasm",
            Status = "done", OverallPercent = 90, Phase = "wasm"
        });

        // مكتمل
        result.Success = true;
        result.Message = $"✅ AlphaApp '{app.Name}' جاهز للتشغيل في المتصفح!";

        progress?.Report(new BuildProgressEvent
        {
            StepNumber = 9, TotalSteps = 9, StepName = "اكتمل البناء",
            Status = "done", OverallPercent = 100, Phase = "complete",
            Message = result.Message
        });

        _logger.LogInformation("🎉 ═══════════════════════════════════════");
        _logger.LogInformation("🎉 {Message}", result.Message);
        _logger.LogInformation("🎉 مشغّل: {PlayerPath}", result.WasmPackage.PlayerHtmlPath);
        _logger.LogInformation("🎉 ═══════════════════════════════════════");

        return result;
    }
}

public class AlphaAppResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? Error { get; set; }
    public AlphaAppDefinition App { get; set; } = new();
    public PipelineResult? PipelineResult { get; set; }
    public WasmPackage? WasmPackage { get; set; }
}
