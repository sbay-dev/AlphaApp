using System.IO.Compression;
using System.Text.Json;
using AlphaApp.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlphaApp.Core.Services;

/// <summary>
/// جسر qemu-wasm: يجهّز اللقطات للتشغيل في المتصفح
/// يولّد ملفات التهيئة + HTML/JS اللازمة لـ qemu-wasm
/// </summary>
public class WasmBridgeService
{
    private readonly QemuOptions _options;
    private readonly ILogger<WasmBridgeService> _logger;

    public WasmBridgeService(IOptions<QemuOptions> options, ILogger<WasmBridgeService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// تحويل لقطة إلى حزمة متوافقة مع qemu-wasm
    /// تُنشئ مجلد يحتوي: config.json + disk image + تعليمات التشغيل
    /// </summary>
    public async Task<WasmPackage> PrepareForWasmAsync(SnapshotInfo snapshot, CancellationToken ct = default)
    {
        _logger.LogInformation("🌐 تجهيز لقطة {Id} لـ qemu-wasm...", snapshot.Id);

        var outputDir = Path.Combine(_options.SnapshotsDirectory, "wasm", snapshot.Id);
        Directory.CreateDirectory(outputDir);

        // إنشاء تهيئة qemu-wasm
        var wasmConfig = new WasmConfig
        {
            SnapshotId = snapshot.Id,
            AppName = snapshot.AppName,
            Architecture = snapshot.Architecture,
            MemoryMB = snapshot.MemoryMB,
            GuestPort = snapshot.GuestPort,
            DiskImageUrl = $"/api/snapshots/{snapshot.Id}/disk",
            SnapshotName = $"alpha-{snapshot.Id}",
            QemuArgs = BuildWasmQemuArgs(snapshot)
        };

        var configPath = Path.Combine(outputDir, "config.json");
        await File.WriteAllTextAsync(configPath,
            JsonSerializer.Serialize(wasmConfig, new JsonSerializerOptions { WriteIndented = true }), ct);

        // إنشاء صفحة HTML للمشغّل
        var htmlPath = Path.Combine(outputDir, "player.html");
        await File.WriteAllTextAsync(htmlPath, GeneratePlayerHtml(wasmConfig), ct);

        // إنشاء سكربت JavaScript للتحميل
        var jsPath = Path.Combine(outputDir, "alpha-loader.js");
        await File.WriteAllTextAsync(jsPath, GenerateLoaderScript(wasmConfig), ct);

        snapshot.IsWasmReady = true;
        snapshot.Status = SnapshotStatus.WasmConverted;

        var package = new WasmPackage
        {
            SnapshotId = snapshot.Id,
            ConfigPath = configPath,
            PlayerHtmlPath = htmlPath,
            LoaderScriptPath = jsPath,
            OutputDirectory = outputDir
        };

        _logger.LogInformation("✅ حزمة wasm جاهزة: {Dir}", outputDir);
        return package;
    }

    /// <summary>
    /// تصدير حزمة .cph — أرشيف مضغوط متوافق مع Cepha
    /// يحتوي: manifest.json + disk.qcow2 + player.html + alpha-loader.js + config.json
    /// يمكن فتحه مباشرة عبر cepha أو نشره على أي CDN
    /// </summary>
    public async Task<string> ExportCphAsync(SnapshotInfo snapshot, WasmPackage wasmPackage, CancellationToken ct = default)
    {
        _logger.LogInformation("📦 تصدير حزمة .cph لـ {Name}...", snapshot.AppName);

        var cphFileName = $"{snapshot.AppName}-{snapshot.Id}.cph";
        var cphPath = Path.Combine(wasmPackage.OutputDirectory, cphFileName);

        if (File.Exists(cphPath)) File.Delete(cphPath);

        using (var zip = ZipFile.Open(cphPath, ZipArchiveMode.Create))
        {
            // manifest.json — البيان الرئيسي للحزمة
            var manifest = new
            {
                format = "cepha-alpha-app",
                version = "1.0",
                app = new
                {
                    name = snapshot.AppName,
                    id = snapshot.AppId,
                    snapshotId = snapshot.Id,
                    architecture = snapshot.Architecture,
                    memoryMB = snapshot.MemoryMB,
                    guestPort = snapshot.GuestPort,
                    createdAt = snapshot.CreatedAt.ToString("O")
                },
                runtime = new
                {
                    engine = "qemu-wasm",
                    snapshotName = $"alpha-{snapshot.Id}",
                    diskImage = "disk.qcow2",
                    entryPoint = "player.html"
                },
                checksum = snapshot.Checksum
            };

            var manifestEntry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
            await using (var ms = manifestEntry.Open())
            {
                await JsonSerializer.SerializeAsync(ms, manifest,
                    new JsonSerializerOptions { WriteIndented = true }, ct);
            }

            // صورة القرص المُجمّدة
            if (File.Exists(snapshot.DiskImagePath))
                zip.CreateEntryFromFile(snapshot.DiskImagePath, "disk.qcow2", CompressionLevel.Optimal);

            // ملفات المشغّل
            if (File.Exists(wasmPackage.PlayerHtmlPath))
                zip.CreateEntryFromFile(wasmPackage.PlayerHtmlPath, "player.html", CompressionLevel.Optimal);

            if (File.Exists(wasmPackage.LoaderScriptPath))
                zip.CreateEntryFromFile(wasmPackage.LoaderScriptPath, "alpha-loader.js", CompressionLevel.Optimal);

            if (File.Exists(wasmPackage.ConfigPath))
                zip.CreateEntryFromFile(wasmPackage.ConfigPath, "config.json", CompressionLevel.Optimal);
        }

        var sizeBytes = new FileInfo(cphPath).Length;
        _logger.LogInformation("✅ حزمة .cph جاهزة: {Path} ({Size:F1} MB)", cphPath, sizeBytes / 1048576.0);

        wasmPackage.CphFilePath = cphPath;
        return cphPath;
    }

    /// <summary>بناء أوامر QEMU المتوافقة مع qemu-wasm</summary>
    private List<string> BuildWasmQemuArgs(SnapshotInfo snapshot)
    {
        return
        [
            "-m", $"{snapshot.MemoryMB}",
            "-smp", "1",
            "-drive", "file=disk.qcow2,format=qcow2,if=virtio",
            "-netdev", $"user,id=net0,hostfwd=tcp::{snapshot.GuestPort}-:{snapshot.GuestPort}",
            "-device", "virtio-net-pci,netdev=net0",
            "-nographic",
            "-loadvm", $"alpha-{snapshot.Id}"
        ];
    }

    /// <summary>توليد صفحة HTML لمشغّل qemu-wasm</summary>
    private string GeneratePlayerHtml(WasmConfig config)
    {
        return $$"""
            <!DOCTYPE html>
            <html lang="ar" dir="rtl">
            <head>
                <meta charset="UTF-8">
                <meta name="viewport" content="width=device-width, initial-scale=1.0">
                <title>AlphaApp — {{config.AppName}}</title>
                <style>
                    * { margin: 0; padding: 0; box-sizing: border-box; }
                    body {
                        font-family: 'Segoe UI', Tahoma, sans-serif;
                        background: #0a0a1a;
                        color: #e0e0e0;
                        min-height: 100vh;
                        display: flex;
                        flex-direction: column;
                    }
                    .header {
                        background: linear-gradient(135deg, #1a1a2e 0%, #16213e 100%);
                        padding: 16px 24px;
                        display: flex;
                        align-items: center;
                        gap: 16px;
                        border-bottom: 2px solid #0f3460;
                    }
                    .header h1 { font-size: 1.2rem; color: #00d4ff; }
                    .header .status {
                        margin-right: auto;
                        padding: 4px 12px;
                        border-radius: 12px;
                        font-size: 0.8rem;
                        font-weight: bold;
                    }
                    .status-loading { background: #e67e22; color: #000; }
                    .status-running { background: #2ecc71; color: #000; }
                    .status-error { background: #e74c3c; color: #fff; }
                    .controls {
                        display: flex;
                        gap: 8px;
                        padding: 12px 24px;
                        background: #0d1117;
                        border-bottom: 1px solid #21262d;
                    }
                    .controls button {
                        padding: 6px 16px;
                        border: 1px solid #30363d;
                        border-radius: 6px;
                        background: #21262d;
                        color: #c9d1d9;
                        cursor: pointer;
                        font-size: 0.85rem;
                        transition: all 0.2s;
                    }
                    .controls button:hover { background: #30363d; border-color: #00d4ff; }
                    .controls button.primary { background: #0f3460; border-color: #00d4ff; color: #00d4ff; }
                    .main-container {
                        flex: 1;
                        display: flex;
                        position: relative;
                    }
                    #vm-display {
                        flex: 1;
                        background: #000;
                        display: flex;
                        align-items: center;
                        justify-content: center;
                    }
                    #vm-display canvas { max-width: 100%; max-height: 100%; }
                    #vm-display iframe {
                        width: 100%;
                        height: 100%;
                        border: none;
                    }
                    .sidebar {
                        width: 300px;
                        background: #0d1117;
                        border-right: 1px solid #21262d;
                        padding: 16px;
                        overflow-y: auto;
                    }
                    .sidebar h3 { color: #00d4ff; margin-bottom: 12px; font-size: 0.9rem; }
                    .info-row {
                        display: flex;
                        justify-content: space-between;
                        padding: 6px 0;
                        border-bottom: 1px solid #21262d;
                        font-size: 0.82rem;
                    }
                    .info-label { color: #8b949e; }
                    .info-value { color: #c9d1d9; font-family: monospace; }
                    #console-output {
                        background: #000;
                        color: #0f0;
                        font-family: 'Courier New', monospace;
                        font-size: 0.8rem;
                        padding: 12px;
                        margin-top: 16px;
                        border-radius: 6px;
                        height: 200px;
                        overflow-y: auto;
                        white-space: pre-wrap;
                    }
                    .loading-overlay {
                        position: absolute;
                        inset: 0;
                        background: rgba(0,0,0,0.85);
                        display: flex;
                        flex-direction: column;
                        align-items: center;
                        justify-content: center;
                        gap: 16px;
                        z-index: 10;
                    }
                    .spinner {
                        width: 48px; height: 48px;
                        border: 4px solid #21262d;
                        border-top-color: #00d4ff;
                        border-radius: 50%;
                        animation: spin 1s linear infinite;
                    }
                    @keyframes spin { to { transform: rotate(360deg); } }
                    .progress-bar {
                        width: 300px; height: 6px;
                        background: #21262d;
                        border-radius: 3px;
                        overflow: hidden;
                    }
                    .progress-fill {
                        height: 100%;
                        background: #00d4ff;
                        transition: width 0.3s;
                    }
                </style>
            </head>
            <body>
                <div class="header">
                    <h1>⚡ {{config.AppName}}</h1>
                    <span class="status status-loading" id="status-badge">جاري التحميل...</span>
                    <span style="font-size:0.75rem;color:#8b949e">AlphaApp Player</span>
                </div>
                <div class="controls">
                    <button class="primary" onclick="alphaApp.start()" id="btn-start">▶️ تشغيل</button>
                    <button onclick="alphaApp.pause()">⏸️ إيقاف مؤقت</button>
                    <button onclick="alphaApp.resume()">▶️ استئناف</button>
                    <button onclick="alphaApp.openApp()">🌐 فتح التطبيق</button>
                    <button onclick="alphaApp.fullscreen()">⛶ شاشة كاملة</button>
                </div>
                <div class="main-container">
                    <div class="sidebar">
                        <h3>📊 معلومات التطبيق</h3>
                        <div class="info-row">
                            <span class="info-label">التطبيق</span>
                            <span class="info-value">{{config.AppName}}</span>
                        </div>
                        <div class="info-row">
                            <span class="info-label">المعمارية</span>
                            <span class="info-value">{{config.Architecture}}</span>
                        </div>
                        <div class="info-row">
                            <span class="info-label">الذاكرة</span>
                            <span class="info-value">{{config.MemoryMB}} MB</span>
                        </div>
                        <div class="info-row">
                            <span class="info-label">المنفذ</span>
                            <span class="info-value">{{config.GuestPort}}</span>
                        </div>
                        <div class="info-row">
                            <span class="info-label">اللقطة</span>
                            <span class="info-value">{{config.SnapshotId}}</span>
                        </div>
                        <div class="info-row">
                            <span class="info-label">الحالة</span>
                            <span class="info-value" id="vm-state">غير مبدوء</span>
                        </div>
                        <div id="console-output">AlphaApp Console v1.0\nجاهز للتشغيل...\n</div>
                    </div>
                    <div id="vm-display">
                        <div class="loading-overlay" id="loading">
                            <div class="spinner"></div>
                            <div style="color:#00d4ff;font-size:1.1rem">جاري تحميل {{config.AppName}}...</div>
                            <div class="progress-bar">
                                <div class="progress-fill" id="progress" style="width:0%"></div>
                            </div>
                            <div style="color:#8b949e;font-size:0.8rem" id="progress-text">تهيئة qemu-wasm...</div>
                        </div>
                    </div>
                </div>

                <script>
                    const ALPHA_CONFIG = {{JsonSerializer.Serialize(config)}};
                </script>
                <script src="alpha-loader.js"></script>
            </body>
            </html>
            """;
    }

    /// <summary>توليد سكربت JavaScript لتحميل وتشغيل qemu-wasm</summary>
    private string GenerateLoaderScript(WasmConfig config)
    {
        return $$"""
            /**
             * AlphaApp Loader — يربط qemu-wasm مع لقطة التطبيق المجمّدة
             * عند التحميل: يُنزّل صورة القرص → يُهيئ qemu-wasm → يُحمّل اللقطة → إقلاع فوري
             */
            class AlphaAppPlayer {
                constructor(config) {
                    this.config = config;
                    this.emulator = null;
                    this.isRunning = false;
                    this.console = document.getElementById('console-output');
                    this.log('تهيئة AlphaApp Player...');
                }

                log(msg) {
                    const time = new Date().toLocaleTimeString('ar-SA');
                    if (this.console) {
                        this.console.textContent += `[${time}] ${msg}\n`;
                        this.console.scrollTop = this.console.scrollHeight;
                    }
                    console.log(`[AlphaApp] ${msg}`);
                }

                updateStatus(status, cssClass) {
                    const badge = document.getElementById('status-badge');
                    if (badge) {
                        badge.textContent = status;
                        badge.className = `status ${cssClass}`;
                    }
                }

                updateProgress(percent, text) {
                    const bar = document.getElementById('progress');
                    const label = document.getElementById('progress-text');
                    if (bar) bar.style.width = `${percent}%`;
                    if (label) label.textContent = text;
                }

                async start() {
                    try {
                        this.log('⬇️ تحميل صورة القرص...');
                        this.updateProgress(10, 'تحميل صورة القرص...');

                        // تحميل صورة القرص من الخادم
                        const diskResponse = await fetch(this.config.diskImageUrl);
                        if (!diskResponse.ok) throw new Error(`فشل تحميل القرص: ${diskResponse.status}`);

                        const diskBlob = await diskResponse.blob();
                        const diskBuffer = await diskBlob.arrayBuffer();
                        this.log(`✅ صورة القرص: ${(diskBuffer.byteLength / 1048576).toFixed(1)} MB`);
                        this.updateProgress(40, 'تهيئة qemu-wasm...');

                        // تهيئة qemu-wasm
                        this.log('🔧 تهيئة QEMU WebAssembly...');
                        await this.initQemuWasm(diskBuffer);
                        this.updateProgress(70, 'تحميل اللقطة المجمّدة...');

                        // تحميل اللقطة (loadvm) — هنا يبدأ الإقلاع الفوري
                        this.log('📸 تحميل اللقطة المجمّدة (loadvm)...');
                        await this.loadSnapshot();
                        this.updateProgress(100, 'التطبيق يعمل!');

                        // إخفاء شاشة التحميل
                        const loading = document.getElementById('loading');
                        if (loading) loading.style.display = 'none';

                        this.isRunning = true;
                        this.updateStatus('يعمل', 'status-running');
                        document.getElementById('vm-state').textContent = 'يعمل';
                        this.log('🚀 التطبيق يعمل! (إقلاع فوري من اللقطة)');

                    } catch (error) {
                        this.log(`❌ خطأ: ${error.message}`);
                        this.updateStatus('خطأ', 'status-error');
                        this.updateProgress(0, `خطأ: ${error.message}`);
                    }
                }

                async initQemuWasm(diskBuffer) {
                    // التحقق من دعم SharedArrayBuffer (مطلوب لـ qemu-wasm)
                    if (typeof SharedArrayBuffer === 'undefined') {
                        this.log('⚠️ SharedArrayBuffer غير متوفر — تحقق من COOP/COEP headers');
                    }

                    // محاولة تحميل qemu-wasm
                    if (typeof QemuWasm !== 'undefined') {
                        this.emulator = new QemuWasm({
                            wasm_path: '/lib/qemu-wasm/',
                            memory_size: this.config.memoryMB,
                            vga: 'none',
                            net: 'user',
                            drives: [{
                                type: 'disk',
                                data: new Uint8Array(diskBuffer),
                                format: 'qcow2'
                            }],
                            cmdline: this.config.qemuArgs.join(' '),
                            on_serial: (char) => this.onSerialOutput(char),
                            on_ready: () => this.onVmReady()
                        });
                    } else {
                        // وضع المحاكاة — لعرض الواجهة بدون qemu-wasm فعلي
                        this.log('ℹ️ qemu-wasm غير محمّل — وضع المعاينة');
                        this.emulator = this.createMockEmulator();
                    }
                }

                async loadSnapshot() {
                    if (this.emulator && this.emulator.loadvm) {
                        await this.emulator.loadvm(this.config.snapshotName);
                        this.log('✅ اللقطة محمّلة — الضيف يستأنف من نقطة التجميد');
                    } else {
                        this.log('ℹ️ (محاكاة) اللقطة ستُحمّل عند توفر qemu-wasm');
                        await new Promise(r => setTimeout(r, 1500));
                    }
                }

                onSerialOutput(char) {
                    if (this.console) this.console.textContent += char;
                }

                onVmReady() {
                    this.log('🎉 VM جاهز!');
                }

                pause() {
                    if (this.emulator?.stop) this.emulator.stop();
                    this.updateStatus('متوقف مؤقتاً', 'status-loading');
                    this.log('⏸️ إيقاف مؤقت');
                }

                resume() {
                    if (this.emulator?.resume) this.emulator.resume();
                    this.updateStatus('يعمل', 'status-running');
                    this.log('▶️ استئناف');
                }

                openApp() {
                    const port = this.config.guestPort;
                    window.open(`http://localhost:${port}`, '_blank');
                    this.log(`🌐 فتح التطبيق على المنفذ ${port}`);
                }

                fullscreen() {
                    const display = document.getElementById('vm-display');
                    if (display.requestFullscreen) display.requestFullscreen();
                }

                createMockEmulator() {
                    return {
                        stop: () => this.log('(mock) stopped'),
                        resume: () => this.log('(mock) resumed'),
                        loadvm: async (name) => this.log(`(mock) loadvm ${name}`)
                    };
                }
            }

            // تهيئة المشغّل
            const alphaApp = new AlphaAppPlayer(ALPHA_CONFIG);
            """;
    }
}

/// <summary>تهيئة qemu-wasm</summary>
public class WasmConfig
{
    public string SnapshotId { get; set; } = string.Empty;
    public string AppName { get; set; } = string.Empty;
    public string Architecture { get; set; } = "x86_64";
    public int MemoryMB { get; set; } = 256;
    public int GuestPort { get; set; } = 5000;
    public string DiskImageUrl { get; set; } = string.Empty;
    public string SnapshotName { get; set; } = string.Empty;
    public List<string> QemuArgs { get; set; } = [];
}

/// <summary>حزمة wasm جاهزة للنشر</summary>
public class WasmPackage
{
    public string SnapshotId { get; set; } = string.Empty;
    public string ConfigPath { get; set; } = string.Empty;
    public string PlayerHtmlPath { get; set; } = string.Empty;
    public string LoaderScriptPath { get; set; } = string.Empty;
    public string OutputDirectory { get; set; } = string.Empty;
    public string? CphFilePath { get; set; }
}
