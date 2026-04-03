using AlphaApp.Core.Services;
using AlphaApp.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace AlphaApp.Web.Controllers;

public class HomeController : Controller
{
    private readonly ISnapshotService _snapshots;

    public HomeController(ISnapshotService snapshots) => _snapshots = snapshots;

    public async Task<IActionResult> Index()
    {
        var snapshots = await _snapshots.ListSnapshotsAsync();

        var vm = new DashboardViewModel
        {
            TotalSnapshots = snapshots.Count,
            ReadySnapshots = snapshots.Count(s => s.Status == Core.Models.SnapshotStatus.WasmConverted),
        };

        return View(vm);
    }

    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = HttpContext.TraceIdentifier });
    }
}
