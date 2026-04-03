using AlphaApp.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using AlphaApp.Core.Models;

namespace AlphaApp.Web.Controllers;

/// <summary>
/// مشغّل التطبيق — تشغيل VM من لقطة مجمّدة
/// </summary>
public class PlayerController : Controller
{
    private readonly ISnapshotService _snapshots;
    private readonly WasmBridgeService _wasmBridge;
    private readonly QemuManager _qemuManager;
    private readonly QemuOptions _options;

    public PlayerController(
        ISnapshotService snapshots,
        WasmBridgeService wasmBridge,
        IQemuManager qemuManager,
        IOptions<QemuOptions> options)
    {
        _snapshots = snapshots;
        _wasmBridge = wasmBridge;
        _qemuManager = (QemuManager)qemuManager;
        _options = options.Value;
    }

    /// <summary>صفحة المشغّل</summary>
    public async Task<IActionResult> Index(string id)
    {
        var snapshot = await _snapshots.GetSnapshotAsync(id);
        if (snapshot == null) return NotFound("اللقطة غير موجودة");

        if (!snapshot.IsWasmReady)
        {
            await _wasmBridge.PrepareForWasmAsync(snapshot);
        }

        var runningGuest = _qemuManager.GetRunningGuest(id);
        ViewBag.IsRunning = runningGuest != null;
        ViewBag.HostPort = runningGuest?.HostPort ?? 0;

        return View(snapshot);
    }

    /// <summary>API: تشغيل VM من اللقطة المجمّدة (إقلاع فوري)</summary>
    [HttpPost("/api/snapshots/{id}/launch")]
    public async Task<IActionResult> Launch(string id)
    {
        var snapshot = await _snapshots.GetSnapshotAsync(id);
        if (snapshot == null) return NotFound(new { error = "اللقطة غير موجودة" });

        if (!System.IO.File.Exists(snapshot.DiskImagePath))
            return NotFound(new { error = "صورة القرص غير موجودة" });

        var existing = _qemuManager.GetRunningGuest(id);
        if (existing != null)
            return Ok(new { status = "running", hostPort = existing.HostPort, guestPort = existing.GuestPort, pid = existing.ProcessId });

        try
        {
            var guest = await _qemuManager.LaunchFromSnapshotAsync(snapshot);
            return Ok(new { status = "running", hostPort = guest.HostPort, guestPort = guest.GuestPort, pid = guest.ProcessId });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>API: إيقاف VM يعمل</summary>
    [HttpPost("/api/snapshots/{id}/stop")]
    public async Task<IActionResult> Stop(string id)
    {
        var guest = _qemuManager.GetRunningGuest(id);
        if (guest == null) return Ok(new { status = "stopped" });

        try
        {
            await _qemuManager.StopGuestAsync(guest);
            return Ok(new { status = "stopped" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>API: حالة VM</summary>
    [HttpGet("/api/snapshots/{id}/status")]
    public IActionResult Status(string id)
    {
        var guest = _qemuManager.GetRunningGuest(id);
        if (guest == null)
            return Ok(new { status = "stopped" });

        return Ok(new
        {
            status = guest.Status.ToString().ToLower(),
            hostPort = guest.HostPort,
            guestPort = guest.GuestPort,
            pid = guest.ProcessId,
            startedAt = guest.StartedAt
        });
    }

    /// <summary>API: تنزيل صورة القرص</summary>
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
