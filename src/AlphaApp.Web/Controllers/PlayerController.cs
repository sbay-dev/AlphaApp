using AlphaApp.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using AlphaApp.Core.Models;

namespace AlphaApp.Web.Controllers;

/// <summary>
/// مشغّل التطبيق في المتصفح عبر qemu-wasm
/// </summary>
public class PlayerController : Controller
{
    private readonly ISnapshotService _snapshots;
    private readonly WasmBridgeService _wasmBridge;
    private readonly QemuOptions _options;

    public PlayerController(
        ISnapshotService snapshots,
        WasmBridgeService wasmBridge,
        IOptions<QemuOptions> options)
    {
        _snapshots = snapshots;
        _wasmBridge = wasmBridge;
        _options = options.Value;
    }

    /// <summary>صفحة المشغّل — تعرض qemu-wasm player مع اللقطة</summary>
    public async Task<IActionResult> Index(string id)
    {
        var snapshot = await _snapshots.GetSnapshotAsync(id);
        if (snapshot == null) return NotFound("اللقطة غير موجودة");

        if (!snapshot.IsWasmReady)
        {
            await _wasmBridge.PrepareForWasmAsync(snapshot);
        }

        return View(snapshot);
    }

    /// <summary>API: تنزيل صورة القرص (يستدعيها المتصفح)</summary>
    [HttpGet("/api/snapshots/{id}/disk")]
    public async Task<IActionResult> DownloadDisk(string id)
    {
        var snapshot = await _snapshots.GetSnapshotAsync(id);
        if (snapshot == null) return NotFound();

        if (!System.IO.File.Exists(snapshot.DiskImagePath))
            return NotFound("صورة القرص غير موجودة");

        var stream = System.IO.File.OpenRead(snapshot.DiskImagePath);
        return File(stream, "application/octet-stream", $"{id}.qcow2");
    }

    /// <summary>API: معلومات اللقطة</summary>
    [HttpGet("/api/snapshots/{id}/info")]
    public async Task<IActionResult> Info(string id)
    {
        var snapshot = await _snapshots.GetSnapshotAsync(id);
        if (snapshot == null) return NotFound();
        return Json(snapshot);
    }

    /// <summary>API: قائمة اللقطات الجاهزة</summary>
    [HttpGet("/api/snapshots")]
    public async Task<IActionResult> List()
    {
        var snapshots = await _snapshots.ListSnapshotsAsync();
        return Json(snapshots);
    }
}
