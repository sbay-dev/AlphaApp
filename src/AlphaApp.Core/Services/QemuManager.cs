using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AlphaApp.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlphaApp.Core.Services;

public class QemuManager : IQemuManager, IDisposable
{
    private readonly QemuOptions _options;
    private readonly ILogger<QemuManager> _logger;
    private readonly Dictionary<string, Process> _processes = [];
    private readonly Dictionary<string, (TcpClient client, NetworkStream stream)> _qmpConnections = [];
    private readonly Dictionary<string, GuestState> _activeGuests = [];
    private int _nextQmpPort;
    private int _nextHostPort;

    public QemuManager(IOptions<QemuOptions> options, ILogger<QemuManager> logger)
    {
        _options = options.Value;
        _logger = logger;
        _nextQmpPort = _options.QmpBasePort;
        _nextHostPort = _options.HostPortBase;
        Directory.CreateDirectory(_options.SnapshotsDirectory);
    }

    public async Task<GuestState> BootGuestAsync(AlphaAppDefinition app, string diskImagePath, CancellationToken ct = default)
    {
        var qmpPort = Interlocked.Increment(ref _nextQmpPort);
        var hostPort = Interlocked.Increment(ref _nextHostPort);
        var guestId = $"alpha-{app.Id}";

        var guest = new GuestState
        {
            GuestId = guestId,
            AppId = app.Id,
            QmpPort = qmpPort,
            HostPort = hostPort,
            GuestPort = app.GuestPort,
            Status = GuestStatus.Starting
        };

        var qemuBin = ResolveQemuBinary(app.Architecture);
        var args = BuildQemuArgs(app, diskImagePath, qmpPort, hostPort);

        _logger.LogInformation("⚡ إقلاع ضيف {GuestId}: {Bin} {Args}", guestId, qemuBin, string.Join(" ", args));

        var psi = new ProcessStartInfo
        {
            FileName = qemuBin,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"فشل تشغيل QEMU للضيف {guestId}");

        guest.ProcessId = process.Id;
        guest.StartedAt = DateTime.UtcNow;
        guest.Status = GuestStatus.Running;
        _processes[guestId] = process;
        _activeGuests[guestId] = guest;

        // انتظار فتح منفذ QMP
        await WaitForQmpReadyAsync(qmpPort, 30, ct);

        // إرسال qmp_capabilities للتفعيل (عبر اتصال مستمر)
        await InitQmpSessionAsync(guestId, qmpPort, ct);

        _logger.LogInformation("✅ الضيف {GuestId} يعمل — QMP:{QmpPort} Host:{HostPort}→Guest:{GuestPort}",
            guestId, qmpPort, hostPort, app.GuestPort);

        return guest;
    }

    /// <summary>تهيئة جلسة QMP مستمرة — ترحيب + قدرات</summary>
    private async Task InitQmpSessionAsync(string guestId, int qmpPort, CancellationToken ct)
    {
        var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", qmpPort, ct);
        var stream = client.GetStream();
        stream.ReadTimeout = 10_000;
        stream.WriteTimeout = 5_000;

        // قراءة رسالة الترحيب
        var buffer = new byte[4096];
        await stream.ReadAsync(buffer, ct);

        // إرسال qmp_capabilities
        var capsJson = JsonSerializer.Serialize(new { execute = "qmp_capabilities" });
        await stream.WriteAsync(Encoding.UTF8.GetBytes(capsJson + "\n"), ct);
        await Task.Delay(300, ct);
        await stream.ReadAsync(buffer, ct);

        _qmpConnections[guestId] = (client, stream);
        _logger.LogDebug("QMP session established for {GuestId}", guestId);
    }

    /// <summary>إرسال أمر QMP عبر الجلسة المستمرة</summary>
    private async Task<QmpResponse> SendQmpOnSessionAsync(string guestId, QmpCommand command, CancellationToken ct)
    {
        if (!_qmpConnections.TryGetValue(guestId, out var conn) || !conn.client.Connected)
            throw new InvalidOperationException($"لا توجد جلسة QMP لـ {guestId}");

        string json;
        if (command.Arguments != null)
            json = JsonSerializer.Serialize(new { execute = command.Execute, arguments = command.Arguments });
        else
            json = JsonSerializer.Serialize(new { execute = command.Execute });

        var data = Encoding.UTF8.GetBytes(json + "\n");
        await conn.stream.WriteAsync(data, ct);

        await Task.Delay(500, ct);
        var buffer = new byte[8192];
        var read = await conn.stream.ReadAsync(buffer, ct);
        var responseJson = Encoding.UTF8.GetString(buffer, 0, read);

        _logger.LogDebug("QMP [{GuestId}] → {Cmd} ← {Response}", guestId, command.Execute, responseJson.Trim());

        if (responseJson.Contains("\"error\""))
        {
            return new QmpResponse { Error = new QmpError { Desc = responseJson } };
        }

        return new QmpResponse { Return = responseJson };
    }

    public async Task<bool> FreezeGuestAsync(GuestState guest, CancellationToken ct = default)
    {
        _logger.LogInformation("🧊 تجميد الضيف {GuestId}...", guest.GuestId);
        guest.Status = GuestStatus.Freezing;

        var response = await SendQmpOnSessionAsync(guest.GuestId, new QmpCommand { Execute = "stop" }, ct);
        if (response.Error != null)
        {
            _logger.LogError("❌ فشل تجميد {GuestId}: {Error}", guest.GuestId, response.Error.Desc);
            guest.Status = GuestStatus.Error;
            guest.LastError = response.Error.Desc;
            return false;
        }

        guest.Status = GuestStatus.Frozen;
        guest.FrozenAt = DateTime.UtcNow;
        _logger.LogInformation("✅ الضيف {GuestId} مجمّد بنجاح", guest.GuestId);
        return true;
    }

    public async Task<bool> ResumeGuestAsync(GuestState guest, CancellationToken ct = default)
    {
        _logger.LogInformation("▶️ استئناف الضيف {GuestId}...", guest.GuestId);

        var response = await SendQmpOnSessionAsync(guest.GuestId, new QmpCommand { Execute = "cont" }, ct);
        if (response.Error != null)
        {
            guest.LastError = response.Error.Desc;
            return false;
        }

        guest.Status = GuestStatus.Running;
        guest.FrozenAt = null;
        return true;
    }

    public async Task<string> SaveVmStateAsync(GuestState guest, string snapshotName, CancellationToken ct = default)
    {
        _logger.LogInformation("💾 حفظ حالة VM {GuestId} → {Snapshot}...", guest.GuestId, snapshotName);
        guest.Status = GuestStatus.Saving;

        var response = await SendQmpOnSessionAsync(guest.GuestId, new QmpCommand
        {
            Execute = "human-monitor-command",
            Arguments = new Dictionary<string, object>
            {
                ["command-line"] = $"savevm {snapshotName}"
            }
        }, ct);

        if (response.Error != null)
        {
            throw new InvalidOperationException($"فشل savevm: {response.Error.Desc}");
        }

        await Task.Delay(2000, ct);

        guest.Status = GuestStatus.Frozen;
        var statePath = Path.Combine(_options.SnapshotsDirectory, $"{guest.GuestId}-{snapshotName}.state");
        _logger.LogInformation("✅ حالة VM محفوظة: {Path}", statePath);
        return statePath;
    }

    public async Task StopGuestAsync(GuestState guest, CancellationToken ct = default)
    {
        _logger.LogInformation("⏹️ إيقاف الضيف {GuestId}...", guest.GuestId);

        try
        {
            await SendQmpOnSessionAsync(guest.GuestId, new QmpCommand { Execute = "quit" }, ct);
        }
        catch { /* الضيف قد يكون أُغلق بالفعل */ }

        // تنظيف جلسة QMP
        if (_qmpConnections.TryGetValue(guest.GuestId, out var conn))
        {
            try { conn.stream.Dispose(); conn.client.Dispose(); } catch { }
            _qmpConnections.Remove(guest.GuestId);
        }

        if (_processes.TryGetValue(guest.GuestId, out var proc))
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                await proc.WaitForExitAsync(ct);
            }
            proc.Dispose();
            _processes.Remove(guest.GuestId);
        }

        guest.Status = GuestStatus.Stopped;
        _activeGuests.Remove(guest.GuestId);
    }

    public async Task<QmpResponse> SendQmpCommandAsync(int qmpPort, QmpCommand command, CancellationToken ct = default)
    {
        // fallback: اتصال لمرة واحدة (للاستخدام الخارجي)
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", qmpPort, ct);
        using var stream = client.GetStream();
        stream.ReadTimeout = 10_000;
        stream.WriteTimeout = 5_000;

        var buffer = new byte[4096];
        await stream.ReadAsync(buffer, ct);

        // إرسال qmp_capabilities أولاً
        var capsJson = JsonSerializer.Serialize(new { execute = "qmp_capabilities" });
        await stream.WriteAsync(Encoding.UTF8.GetBytes(capsJson + "\n"), ct);
        await Task.Delay(300, ct);
        await stream.ReadAsync(buffer, ct);

        // إرسال الأمر الفعلي
        string json;
        if (command.Arguments != null)
            json = JsonSerializer.Serialize(new { execute = command.Execute, arguments = command.Arguments });
        else
            json = JsonSerializer.Serialize(new { execute = command.Execute });
        var data = Encoding.UTF8.GetBytes(json + "\n");
        await stream.WriteAsync(data, ct);

        await Task.Delay(500, ct);
        var read = await stream.ReadAsync(buffer, ct);
        var responseJson = Encoding.UTF8.GetString(buffer, 0, read);

        _logger.LogDebug("QMP [{Port}] → {Cmd} ← {Response}", qmpPort, command.Execute, responseJson.Trim());

        if (responseJson.Contains("\"error\""))
        {
            return new QmpResponse { Error = new QmpError { Desc = responseJson } };
        }

        return new QmpResponse { Return = responseJson };
    }

    public async Task<bool> WaitForGuestReadyAsync(GuestState guest, int timeoutSeconds, CancellationToken ct = default)
    {
        _logger.LogInformation("⏳ انتظار جاهزية التطبيق على المنفذ {Port}...", guest.HostPort);
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed.TotalSeconds < timeoutSeconds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var tcp = new TcpClient();
                var connectTask = tcp.ConnectAsync("127.0.0.1", guest.HostPort, ct).AsTask();
                if (await Task.WhenAny(connectTask, Task.Delay(1000, ct)) == connectTask)
                {
                    if (tcp.Connected)
                    {
                        _logger.LogInformation("✅ التطبيق جاهز على المنفذ {Port} بعد {Elapsed:F1}s",
                            guest.HostPort, sw.Elapsed.TotalSeconds);
                        return true;
                    }
                }
            }
            catch (SocketException) { }

            await Task.Delay(2000, ct);
        }

        _logger.LogWarning("⚠️ انتهت المهلة ({Timeout}s) بدون جاهزية على المنفذ {Port}", timeoutSeconds, guest.HostPort);
        return false;
    }

    /// <summary>تشغيل ضيف من لقطة مجمّدة — إقلاع فوري عبر loadvm</summary>
    public async Task<GuestState> LaunchFromSnapshotAsync(SnapshotInfo snapshot, CancellationToken ct = default)
    {
        var qmpPort = Interlocked.Increment(ref _nextQmpPort);
        var hostPort = Interlocked.Increment(ref _nextHostPort);
        var guestId = $"run-{snapshot.Id}";

        // إذا كان هناك ضيف يعمل بنفس المعرّف، أوقفه أولاً
        if (_processes.TryGetValue(guestId, out var existingProc))
        {
            if (!existingProc.HasExited)
            {
                existingProc.Kill(entireProcessTree: true);
                await existingProc.WaitForExitAsync(ct);
            }
            existingProc.Dispose();
            _processes.Remove(guestId);
            if (_qmpConnections.TryGetValue(guestId, out var oldConn))
            {
                try { oldConn.stream.Dispose(); oldConn.client.Dispose(); } catch { }
                _qmpConnections.Remove(guestId);
            }
            _activeGuests.Remove(guestId);
        }

        var guest = new GuestState
        {
            GuestId = guestId,
            AppId = snapshot.AppId,
            QmpPort = qmpPort,
            HostPort = hostPort,
            GuestPort = snapshot.GuestPort,
            Status = GuestStatus.Starting
        };

        var snapshotName = !string.IsNullOrEmpty(snapshot.SnapshotName)
            ? snapshot.SnapshotName
            : $"alpha-{snapshot.Id}";

        var qemuBin = ResolveQemuBinary(snapshot.Architecture);
        var args = new List<string>
        {
            "-m", $"{snapshot.MemoryMB}M",
            "-smp", $"{_options.CpuCores}",
            "-drive", $"file={snapshot.DiskImagePath},format=qcow2,if=virtio",
            "-netdev", $"user,id=net0,hostfwd=tcp::{hostPort}-:{snapshot.GuestPort}",
            "-device", "virtio-net-pci,netdev=net0",
            "-qmp", $"tcp:127.0.0.1:{qmpPort},server,nowait",
            "-nographic",
            "-serial", "mon:stdio",
            "-loadvm", snapshotName
        };

        if (_options.EnableKvm)
            args.AddRange(["-enable-kvm", "-cpu", "host"]);
        else
            args.AddRange(["-cpu", "max"]);

        _logger.LogInformation("🚀 إقلاع فوري من لقطة {Snapshot}: {Bin}", snapshotName, qemuBin);

        var psi = new ProcessStartInfo
        {
            FileName = qemuBin,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"فشل تشغيل QEMU من اللقطة {snapshotName}");

        guest.ProcessId = process.Id;
        guest.StartedAt = DateTime.UtcNow;
        _processes[guestId] = process;
        _activeGuests[guestId] = guest;

        // انتظار QMP
        await WaitForQmpReadyAsync(qmpPort, 30, ct);
        await InitQmpSessionAsync(guestId, qmpPort, ct);

        // loadvm يستعيد الحالة فوراً — التطبيق يعمل من لحظة التجميد
        guest.Status = GuestStatus.Running;

        _logger.LogInformation("✅ الضيف {GuestId} يعمل من اللقطة — Host:{HostPort}→Guest:{GuestPort}",
            guestId, hostPort, snapshot.GuestPort);

        return guest;
    }

    /// <summary>الحصول على ضيف يعمل بمعرّف اللقطة</summary>
    public GuestState? GetRunningGuest(string snapshotId)
    {
        var guestId = $"run-{snapshotId}";
        if (!_processes.TryGetValue(guestId, out var proc) || proc.HasExited)
            return null;
        return _activeGuests.GetValueOrDefault(guestId);
    }

    private string ResolveQemuBinary(string arch) => arch switch
    {
        "aarch64" or "arm64" => _options.QemuBinaryPath.Replace("x86_64", "aarch64"),
        _ => _options.QemuBinaryPath
    };

    private List<string> BuildQemuArgs(AlphaAppDefinition app, string diskImage, int qmpPort, int hostPort)
    {
        var args = new List<string>
        {
            "-m", $"{app.MemoryMB}M",
            "-smp", $"{_options.CpuCores}",
            "-drive", $"file={diskImage},format=qcow2,if=virtio",
            "-netdev", $"user,id=net0,hostfwd=tcp::{hostPort}-:{app.GuestPort}",
            "-device", "virtio-net-pci,netdev=net0",
            "-qmp", $"tcp:127.0.0.1:{qmpPort},server,nowait",
            "-nographic",
            "-serial", "mon:stdio"
        };

        if (_options.EnableKvm)
            args.AddRange(["-enable-kvm", "-cpu", "host"]);
        else
            args.AddRange(["-cpu", "max"]);

        // تمرير متغيرات البيئة عبر kernel cmdline
        if (app.Environment.Count > 0)
        {
            var envStr = string.Join(" ", app.Environment.Select(e => $"{e.Key}={e.Value}"));
            args.AddRange(["-append", $"console=ttyS0 {envStr}"]);
        }

        return args;
    }

    private async Task WaitForQmpReadyAsync(int port, int timeoutSeconds, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < timeoutSeconds)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync("127.0.0.1", port, ct);
                if (tcp.Connected) return;
            }
            catch { }
            await Task.Delay(500, ct);
        }
        throw new TimeoutException($"QMP لم يستجب على المنفذ {port} خلال {timeoutSeconds}s");
    }

    public void Dispose()
    {
        foreach (var (_, conn) in _qmpConnections)
        {
            try { conn.stream.Dispose(); conn.client.Dispose(); } catch { }
        }
        _qmpConnections.Clear();

        foreach (var (id, proc) in _processes)
        {
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
            proc.Dispose();
        }
        _processes.Clear();
        GC.SuppressFinalize(this);
    }
}
