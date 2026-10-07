using Microsoft.Win32;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WukongBenchmarkAutomation;

internal static class Program
{
    private const string AppName = "Black Myth Wukong Benchmark Tool";

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("Black Myth: Wukong Benchmark Automation");
        Console.WriteLine("======================================\n");

        string? explicitDir = GetArg(args, "--benchmark-dir");
        string? installDir = explicitDir ?? SteamLocator.FindInstallDirectory();

        if (installDir is null)
        {
            Console.Error.WriteLine("Benchmark Tool not found. Install it from Steam or pass --benchmark-dir=\"...\".");
            return 2;
        }

        string exePath = Path.Combine(installDir, "b1_benchmark.exe");
        string savedDir = Path.Combine(installDir, "b1", "Saved");
        string configDir = Path.Combine(savedDir, "Config", "Windows");
        string userSettingsPath = Path.Combine(configDir, "GameUserSettings.ini");
        string engineIniPath = Path.Combine(configDir, "Engine.ini");

        if (!File.Exists(exePath))
        {
            Console.Error.WriteLine($"Executable not found: {exePath}");
            return 3;
        }

        Directory.CreateDirectory(configDir);

        HardwareSnapshot hardware = HardwareProbe.Capture();
        Console.WriteLine(hardware.ToDisplayString());
        Console.WriteLine($"Benchmark directory: {installDir}\n");

        var cpuProfile = BenchmarkProfile.CreateCpuBound();
        var gpuProfile = BenchmarkProfile.CreateGpuBound(hardware.ScreenWidth, hardware.ScreenHeight, hardware.RayTracingLikelySupported);

        using var backup = new ConfigBackup(userSettingsPath, engineIniPath);

        BenchmarkResult cpuResult;
        BenchmarkResult gpuResult;

        try
        {
            cpuResult = RunPass("CPU test", cpuProfile, exePath, savedDir, userSettingsPath, engineIniPath);
            gpuResult = RunPass("GPU test", gpuProfile, exePath, savedDir, userSettingsPath, engineIniPath);
        }
        finally
        {
            backup.Restore();
        }

        var report = new BenchmarkReport(DateTimeOffset.Now, hardware, cpuProfile, cpuResult, gpuProfile, gpuResult);
        string jsonPath = Path.Combine(Environment.CurrentDirectory, $"wukong-benchmark-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        string txtPath = Path.ChangeExtension(jsonPath, ".txt");

        File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, JsonOptions));
        File.WriteAllText(txtPath, report.ToDisplayString());

        Console.WriteLine("\n" + report.ToDisplayString());
        Console.WriteLine($"\nJSON report: {jsonPath}");
        Console.WriteLine($"Text report: {txtPath}");
        return 0;
    }

    private static BenchmarkResult RunPass(
        string title,
        BenchmarkProfile profile,
        string exePath,
        string savedDir,
        string userSettingsPath,
        string engineIniPath)
    {
        Console.WriteLine($"\n--- {title} ---");
        Console.WriteLine(profile.ToDisplayString());

        GameConfigurator.Apply(profile, userSettingsPath, engineIniPath);
        DateTimeOffset started = DateTimeOffset.Now;

        using Process process = BenchmarkRunner.Start(exePath);
        BenchmarkRunner.TryStartBenchmarkFromMainMenu(process);

        BenchmarkResult result = ResultCollector.WaitForResult(savedDir, started, process, TimeSpan.FromMinutes(12));
        BenchmarkRunner.Close(process);

        Console.WriteLine(result.ToDisplayString());
        return result;
    }

    private static string? GetArg(string[] args, string name)
    {
        string prefix = name + "=";
        return args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..].Trim('"');
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

internal sealed record HardwareSnapshot(
    string Cpu,
    int CpuCores,
    int CpuThreads,
    string Gpu,
    double VramGb,
    double RamGb,
    string Os,
    int ScreenWidth,
    int ScreenHeight,
    bool RayTracingLikelySupported)
{
    public string ToDisplayString() =>
        $"CPU: {Cpu} ({CpuCores}C/{CpuThreads}T)\n" +
        $"GPU: {Gpu} ({VramGb:0.#} GB VRAM)\n" +
        $"RAM: {RamGb:0.#} GB\n" +
        $"OS: {Os}\n" +
        $"Desktop: {ScreenWidth}x{ScreenHeight}\n" +
        $"Hardware RT profile: {(RayTracingLikelySupported ? "supported/likely" : "disabled")}";
}

internal static class HardwareProbe
{
    public static HardwareSnapshot Capture()
    {
        string cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Unknown CPU";
        int cores = Environment.ProcessorCount;
        int threads = Environment.ProcessorCount;
        string gpu = "Unknown GPU";
        double vramGb = 0;
        double ramGb = 0;
        string os = Environment.OSVersion.VersionString;

        try
        {
            string script = "$ErrorActionPreference='Stop';" +
                "$cpu=Get-CimInstance Win32_Processor|Select-Object -First 1;" +
                "$gpu=Get-CimInstance Win32_VideoController|Sort-Object AdapterRAM -Descending|Select-Object -First 1;" +
                "$cs=Get-CimInstance Win32_ComputerSystem;" +
                "$os=Get-CimInstance Win32_OperatingSystem;" +
                "[pscustomobject]@{Cpu=$cpu.Name;Cores=$cpu.NumberOfCores;Threads=$cpu.NumberOfLogicalProcessors;Gpu=$gpu.Name;Vram=[double]$gpu.AdapterRAM;Ram=[double]$cs.TotalPhysicalMemory;Os=($os.Caption+' '+$os.Version)}|ConvertTo-Json -Compress";

            string json = RunPowerShell(script);
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            cpu = root.TryGetProperty("Cpu", out var eCpu) ? eCpu.GetString() ?? cpu : cpu;
            cores = root.TryGetProperty("Cores", out var eCores) ? eCores.GetInt32() : cores;
            threads = root.TryGetProperty("Threads", out var eThreads) ? eThreads.GetInt32() : threads;
            gpu = root.TryGetProperty("Gpu", out var eGpu) ? eGpu.GetString() ?? gpu : gpu;
            vramGb = root.TryGetProperty("Vram", out var eVram) ? eVram.GetDouble() / 1024d / 1024d / 1024d : 0;
            ramGb = root.TryGetProperty("Ram", out var eRam) ? eRam.GetDouble() / 1024d / 1024d / 1024d : 0;
            os = root.TryGetProperty("Os", out var eOs) ? eOs.GetString() ?? os : os;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Hardware probe fallback: {ex.Message}");
        }

        int width = NativeMethods.GetSystemMetrics(0);
        int height = NativeMethods.GetSystemMetrics(1);
        if (width <= 0) width = 1920;
        if (height <= 0) height = 1080;

        bool rt = IsLikelyRtCapable(gpu);
        return new HardwareSnapshot(cpu.Trim(), cores, threads, gpu.Trim(), vramGb, ramGb, os.Trim(), width, height, rt);
    }

    private static bool IsLikelyRtCapable(string gpu)
    {
        string s = gpu.ToUpperInvariant();
        if (s.Contains("RTX")) return true;
        if (s.Contains("ARC A") || s.Contains("ARC B")) return true;

        Match amd = Regex.Match(s, @"RX\s*(\d{4})");
        if (amd.Success && int.TryParse(amd.Groups[1].Value, out int model))
            return model >= 6000;

        return false;
    }

    private static string RunPowerShell(string script)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + script.Replace("\"", "\\\"") + "\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using Process p = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start PowerShell.");
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        if (p.ExitCode != 0) throw new InvalidOperationException(stderr.Trim());
        return stdout.Trim();
    }
}

internal sealed record BenchmarkProfile(
    string Name,
    int Width,
    int Height,
    int QualityLevel,
    int ResolutionQuality,
    bool RayTracing,
    bool FrameGeneration,
    string Rationale)
{
    public static BenchmarkProfile CreateCpuBound() => new(
        "CPU-bound",
        1280,
        720,
        0,
        50,
        false,
        false,
        "Low resolution, low scalability, RT/FG off. This intentionally reduces GPU frame time so the CPU is more likely to become the limiting component.");

    public static BenchmarkProfile CreateGpuBound(int width, int height, bool rtSupported)
    {
        width = Math.Max(width, 1920);
        height = Math.Max(height, 1080);
        return new(
            "GPU-bound",
            width,
            height,
            4,
            100,
            rtSupported,
            false,
            rtSupported
                ? "Native desktop resolution, Cinematic scalability and full RT. Frame generation is disabled so measured FPS reflects rendered frames."
                : "Native desktop resolution and Cinematic scalability. RT is disabled because the detected GPU is not confidently identified as hardware-RT capable. Frame generation is disabled.");
    }

    public string ToDisplayString() =>
        $"Profile: {Name}\n" +
        $"Resolution: {Width}x{Height}\n" +
        $"Scalability: {(QualityLevel == 0 ? "Low" : "Cinematic")} ({QualityLevel})\n" +
        $"Resolution quality: {ResolutionQuality}%\n" +
        $"Ray tracing: {(RayTracing ? "On" : "Off")}\n" +
        $"Frame generation: {(FrameGeneration ? "On" : "Off")}\n" +
        $"Reason: {Rationale}";
}

internal static class GameConfigurator
{
    private static readonly string[] ScalabilityKeys =
    {
        "sg.ViewDistanceQuality",
        "sg.AntiAliasingQuality",
        "sg.ShadowQuality",
        "sg.GlobalIlluminationQuality",
        "sg.ReflectionQuality",
        "sg.PostProcessQuality",
        "sg.TextureQuality",
        "sg.EffectsQuality",
        "sg.FoliageQuality",
        "sg.ShadingQuality"
    };

    public static void Apply(BenchmarkProfile profile, string gameUserSettings, string engineIni)
    {
        IniDocument game = IniDocument.Load(gameUserSettings);
        const string engineSection = "/Script/Engine.GameUserSettings";

        game.Set(engineSection, "ResolutionSizeX", profile.Width.ToString(CultureInfo.InvariantCulture));
        game.Set(engineSection, "ResolutionSizeY", profile.Height.ToString(CultureInfo.InvariantCulture));
        game.Set(engineSection, "LastUserConfirmedResolutionSizeX", profile.Width.ToString(CultureInfo.InvariantCulture));
        game.Set(engineSection, "LastUserConfirmedResolutionSizeY", profile.Height.ToString(CultureInfo.InvariantCulture));
        game.Set(engineSection, "bUseVSync", "False");
        game.Set("ScalabilityGroups", "sg.ResolutionQuality", profile.ResolutionQuality.ToString(CultureInfo.InvariantCulture));
        game.Set("ScalabilityGroups", "sg.RayTracingQuality", profile.RayTracing ? "4" : "0");

        foreach (string key in ScalabilityKeys)
            game.Set("ScalabilityGroups", key, profile.QualityLevel.ToString(CultureInfo.InvariantCulture));

        game.ReplaceExistingKeyEverywhere("bEnableRayTracing", profile.RayTracing ? "True" : "False");
        game.ReplaceExistingKeyEverywhere("bRayTracing", profile.RayTracing ? "True" : "False");
        game.ReplaceExistingKeyEverywhere("bEnableFrameGeneration", profile.FrameGeneration ? "True" : "False");
        game.ReplaceExistingKeyEverywhere("bFrameGeneration", profile.FrameGeneration ? "True" : "False");
        game.Save(gameUserSettings);

        IniDocument engine = IniDocument.Load(engineIni);
        engine.Set("SystemSettings", "r.VSync", "0");
        engine.Set("SystemSettings", "t.MaxFPS", "0");
        engine.Set("SystemSettings", "r.ScreenPercentage", profile.ResolutionQuality.ToString(CultureInfo.InvariantCulture));
        engine.Set("SystemSettings", "r.RayTracing", profile.RayTracing ? "1" : "0");
        engine.Set("SystemSettings", "r.Streamline.DLSSG.Enable", profile.FrameGeneration ? "1" : "0");
        engine.Set("SystemSettings", "r.FidelityFX.FSR3.FrameInterpolation", profile.FrameGeneration ? "1" : "0");
        engine.Set("SystemSettings", "r.NGX.DLSS.FrameGeneration", profile.FrameGeneration ? "1" : "0");
        engine.Save(engineIni);
    }
}

internal sealed class IniDocument
{
    private readonly List<string> _lines;
    private IniDocument(List<string> lines) => _lines = lines;

    public static IniDocument Load(string path)
    {
        if (!File.Exists(path)) return new IniDocument(new List<string>());
        return new IniDocument(File.ReadAllLines(path).ToList());
    }

    public void Set(string section, string key, string value)
    {
        string sectionHeader = "[" + section + "]";
        int sectionIndex = _lines.FindIndex(x => x.Trim().Equals(sectionHeader, StringComparison.OrdinalIgnoreCase));

        if (sectionIndex < 0)
        {
            if (_lines.Count > 0 && !string.IsNullOrWhiteSpace(_lines[^1])) _lines.Add(string.Empty);
            _lines.Add(sectionHeader);
            _lines.Add($"{key}={value}");
            return;
        }

        int end = _lines.Count;
        for (int i = sectionIndex + 1; i < _lines.Count; i++)
        {
            if (_lines[i].TrimStart().StartsWith("[", StringComparison.Ordinal))
            {
                end = i;
                break;
            }
        }

        for (int i = sectionIndex + 1; i < end; i++)
        {
            string trimmed = _lines[i].TrimStart();
            if (trimmed.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
            {
                _lines[i] = $"{key}={value}";
                return;
            }
        }

        _lines.Insert(end, $"{key}={value}");
    }

    public void ReplaceExistingKeyEverywhere(string key, string value)
    {
        for (int i = 0; i < _lines.Count; i++)
        {
            string trimmed = _lines[i].TrimStart();
            if (trimmed.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                _lines[i] = $"{key}={value}";
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, _lines, new UTF8Encoding(false));
    }
}

internal static class SteamLocator
{
    private const string RelativeInstall = @"steamapps\common\Black Myth Wukong Benchmark Tool";

    public static string? FindInstallDirectory()
    {
        var steamRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        TryAddRegistryPath(steamRoots, Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
        TryAddRegistryPath(steamRoots, Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        TryAddRegistryPath(steamRoots, Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");

        string[] defaults =
        {
            @"C:\Program Files (x86)\Steam",
            @"C:\Program Files\Steam"
        };
        foreach (string p in defaults) if (Directory.Exists(p)) steamRoots.Add(p);

        var libraries = new HashSet<string>(steamRoots, StringComparer.OrdinalIgnoreCase);
        foreach (string root in steamRoots)
        {
            string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;

            string text = File.ReadAllText(vdf);
            foreach (Match m in Regex.Matches(text, "\\\"path\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase))
            {
                string path = m.Groups[1].Value.Replace("\\\\", "\\");
                if (Directory.Exists(path)) libraries.Add(path);
            }
        }

        foreach (string library in libraries)
        {
            string candidate = Path.Combine(library, RelativeInstall);
            if (File.Exists(Path.Combine(candidate, "b1_benchmark.exe")))
                return candidate;
        }

        return null;
    }

    private static void TryAddRegistryPath(HashSet<string> paths, RegistryKey root, string subKey, string valueName)
    {
        try
        {
            using RegistryKey? key = root.OpenSubKey(subKey);
            if (key?.GetValue(valueName) is string path && Directory.Exists(path))
                paths.Add(path.Replace('/', '\\'));
        }
        catch { }
    }
}

internal static class BenchmarkRunner
{
    public static Process Start(string exePath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
            Arguments = "-NoSplash -NoVSync",
            UseShellExecute = true
        };

        return Process.Start(psi) ?? throw new InvalidOperationException("Unable to start benchmark executable.");
    }

    public static void TryStartBenchmarkFromMainMenu(Process process)
    {
        Console.WriteLine("Waiting for benchmark window...");
        DateTime deadline = DateTime.UtcNow.AddSeconds(45);
        IntPtr hwnd = IntPtr.Zero;

        while (DateTime.UtcNow < deadline && !process.HasExited)
        {
            process.Refresh();
            hwnd = process.MainWindowHandle;
            if (hwnd != IntPtr.Zero) break;
            Thread.Sleep(500);
        }

        if (hwnd == IntPtr.Zero)
        {
            Console.WriteLine("No main window handle detected. The process may already be running the benchmark.");
            return;
        }

        Thread.Sleep(5000);
        NativeMethods.ShowWindow(hwnd, 9);
        NativeMethods.SetForegroundWindow(hwnd);
        Thread.Sleep(500);

        NativeMethods.PostMessage(hwnd, 0x0100, (IntPtr)0x0D, IntPtr.Zero);
        NativeMethods.PostMessage(hwnd, 0x0101, (IntPtr)0x0D, IntPtr.Zero);
        Console.WriteLine("Start command sent to Benchmark Tool.");
    }

    public static void Close(Process process)
    {
        try
        {
            if (process.HasExited) return;
            if (process.CloseMainWindow() && process.WaitForExit(5000)) return;
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch { }
    }
}

internal sealed record BenchmarkResult(
    bool MetricsFound,
    double? AverageFps,
    double? MinFps,
    double? MaxFps,
    double? Low5Fps,
    double? VramGb,
    string? SourceFile,
    TimeSpan Elapsed,
    string Note)
{
    public string ToDisplayString()
    {
        if (!MetricsFound)
            return $"Result: metrics were not found automatically. Elapsed {Elapsed:mm\\:ss}. {Note}";

        return $"Result: Avg {Fmt(AverageFps)} FPS | Min {Fmt(MinFps)} | Max {Fmt(MaxFps)} | Low 5th {Fmt(Low5Fps)} | VRAM {Fmt(VramGb)} GB\n" +
               $"Source: {SourceFile}";
    }

    private static string Fmt(double? value) => value.HasValue ? value.Value.ToString("0.##", CultureInfo.InvariantCulture) : "n/a";
}

internal static class ResultCollector
{
    private static readonly Regex AverageRx = new(@"(?im)(?:average|avg|средн\w*)\s*(?:fps|frame\s*rate|кадр\w*)?\s*[:=]\s*(\d+(?:[\.,]\d+)?)", RegexOptions.Compiled);
    private static readonly Regex MinRx = new(@"(?im)(?:minimum|min|lowest|миним\w*)\s*(?:fps|frame\s*rate|кадр\w*)?\s*[:=]\s*(\d+(?:[\.,]\d+)?)", RegexOptions.Compiled);
    private static readonly Regex MaxRx = new(@"(?im)(?:maximum|max|highest|максим\w*)\s*(?:fps|frame\s*rate|кадр\w*)?\s*[:=]\s*(\d+(?:[\.,]\d+)?)", RegexOptions.Compiled);
    private static readonly Regex Low5Rx = new(@"(?im)(?:lower?\s*5(?:th)?|low\s*5(?:th)?|5(?:th)?\s*percentile|нижн\w*\s*(?:пятая|20%))\s*(?:fps)?\s*[:=]\s*(\d+(?:[\.,]\d+)?)", RegexOptions.Compiled);
    private static readonly Regex VramRx = new(@"(?im)(?:vram|video\s*memory|видеопам\w*)[^\d]{0,20}(\d+(?:[\.,]\d+)?)\s*(gb|gib|mb|mib)?", RegexOptions.Compiled);

    public static BenchmarkResult WaitForResult(string savedDir, DateTimeOffset started, Process process, TimeSpan timeout)
    {
        Stopwatch sw = Stopwatch.StartNew();
        DateTimeOffset notBefore = started.AddSeconds(-2);

        while (sw.Elapsed < timeout)
        {
            BenchmarkResult? parsed = TryParseRecentFiles(savedDir, notBefore, sw.Elapsed);
            if (parsed is not null && parsed.MetricsFound)
            {
                Thread.Sleep(3000);
                return TryParseRecentFiles(savedDir, notBefore, sw.Elapsed) ?? parsed;
            }

            if (process.HasExited)
            {
                parsed = TryParseRecentFiles(savedDir, notBefore, sw.Elapsed);
                return parsed ?? new BenchmarkResult(false, null, null, null, null, null, null, sw.Elapsed,
                    "Benchmark process exited before a machine-readable FPS result was located.");
            }

            Thread.Sleep(2000);
        }

        return TryParseRecentFiles(savedDir, notBefore, sw.Elapsed) ??
            new BenchmarkResult(false, null, null, null, null, null, null, sw.Elapsed,
                "Timed out while waiting for a machine-readable benchmark result. Check b1\\Saved files/logs.");
    }

    private static BenchmarkResult? TryParseRecentFiles(string savedDir, DateTimeOffset notBefore, TimeSpan elapsed)
    {
        if (!Directory.Exists(savedDir)) return null;

        string[] extensions = { ".log", ".txt", ".json", ".csv", ".ini" };
        IEnumerable<FileInfo> files;
        try
        {
            files = new DirectoryInfo(savedDir)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(f => f.LastWriteTimeUtc >= notBefore.UtcDateTime)
                .Where(f => extensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
                .Where(f => f.Length <= 8 * 1024 * 1024)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(40)
                .ToArray();
        }
        catch
        {
            return null;
        }

        foreach (FileInfo file in files)
        {
            string text;
            try { text = ReadTextShared(file.FullName); }
            catch { continue; }

            double? avg = MatchNumber(AverageRx, text);
            double? min = MatchNumber(MinRx, text);
            double? max = MatchNumber(MaxRx, text);
            double? low5 = MatchNumber(Low5Rx, text);
            double? vram = MatchVram(text);

            if (avg.HasValue || (min.HasValue && max.HasValue))
            {
                return new BenchmarkResult(true, avg, min, max, low5, vram, file.FullName, elapsed,
                    "Metrics parsed from a recently modified Benchmark Tool file.");
            }
        }

        return null;
    }

    private static string ReadTextShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, detectEncodingFromByteOrderMarks: true);
        return sr.ReadToEnd();
    }

    private static double? MatchNumber(Regex regex, string text)
    {
        MatchCollection matches = regex.Matches(text);
        if (matches.Count == 0) return null;
        string raw = matches[^1].Groups[1].Value.Replace(',', '.');
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : null;
    }

    private static double? MatchVram(string text)
    {
        MatchCollection matches = VramRx.Matches(text);
        if (matches.Count == 0) return null;
        Match m = matches[^1];
        if (!double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            return null;
        string unit = m.Groups[2].Value.ToLowerInvariant();
        if (unit.StartsWith("m")) value /= 1024d;
        return value;
    }
}

internal sealed record BenchmarkReport(
    DateTimeOffset CreatedAt,
    HardwareSnapshot Hardware,
    BenchmarkProfile CpuProfile,
    BenchmarkResult CpuResult,
    BenchmarkProfile GpuProfile,
    BenchmarkResult GpuResult)
{
    public string ToDisplayString() =>
        "BLACK MYTH: WUKONG BENCHMARK REPORT\n" +
        "==================================\n" +
        $"Created: {CreatedAt:yyyy-MM-dd HH:mm:ss zzz}\n\n" +
        "SYSTEM\n------\n" + Hardware.ToDisplayString() + "\n\n" +
        "CPU TEST\n--------\n" + CpuProfile.ToDisplayString() + "\n" + CpuResult.ToDisplayString() + "\n\n" +
        "GPU TEST\n--------\n" + GpuProfile.ToDisplayString() + "\n" + GpuResult.ToDisplayString();
}

internal sealed class ConfigBackup : IDisposable
{
    private readonly List<(string Path, bool Existed, byte[]? Data)> _items = new();
    private bool _restored;

    public ConfigBackup(params string[] paths)
    {
        foreach (string path in paths)
        {
            bool existed = File.Exists(path);
            byte[]? data = existed ? File.ReadAllBytes(path) : null;
            _items.Add((path, existed, data));
        }
    }

    public void Restore()
    {
        if (_restored) return;
        _restored = true;

        foreach (var item in _items)
        {
            try
            {
                if (item.Existed)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(item.Path)!);
                    File.WriteAllBytes(item.Path, item.Data!);
                }
                else if (File.Exists(item.Path))
                {
                    File.Delete(item.Path);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to restore {item.Path}: {ex.Message}");
            }
        }
    }

    public void Dispose() => Restore();
}

internal static class NativeMethods
{
    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
