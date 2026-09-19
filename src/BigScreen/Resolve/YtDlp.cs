using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace BigScreen.Resolve;

/// <summary>
/// Turns a YouTube (or any yt-dlp supported) page URL into a direct media URL that
/// Unity's VideoPlayer can stream.
///
/// Why each player resolves for themselves: the googlevideo.com URLs yt-dlp returns are
/// bound to the IP address that requested them and expire after a few hours. The host
/// therefore shares the *page* URL and every peer runs yt-dlp locally.
///
/// Runs entirely on a thread-pool thread and must not touch Unity/IL2CPP objects; the
/// caller marshals the result back with <see cref="Util.MainThread"/>.
/// </summary>
internal static class YtDlp
{
    public sealed class Result
    {
        public bool Ok;
        public string Error;
        public string Title;
        public double Duration;
        public string DirectUrl;
        public string YtDlpVersion;
    }

    private const string WindowsDownloadUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    private const string LinuxDownloadUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp";

    private static readonly SemaphoreSlim DownloadLock = new(1, 1);

    public static string PluginDirectory =>
        Path.GetDirectoryName(typeof(YtDlp).Assembly.Location) ?? BepInEx.Paths.PluginPath;

    public static string BinaryPath
    {
        get
        {
            var configured = Plugin.YtDlpPath.Value;
            if (!string.IsNullOrWhiteSpace(configured)) return configured;
            var name = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "yt-dlp.exe" : "yt-dlp";
            return Path.Combine(PluginDirectory, name);
        }
    }

    public static bool IsAvailable => File.Exists(BinaryPath);

    public static bool LooksLikeUrl(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        return s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Makes sure the binary exists, downloading it if allowed. Thread-safe.</summary>
    public static async Task<string> EnsureBinaryAsync(CancellationToken ct)
    {
        var path = BinaryPath;
        if (File.Exists(path)) return null;
        if (!Plugin.YtDlpAutoDownload.Value)
            return $"yt-dlp not found at {path} and AutoDownload is off. Download it from https://github.com/yt-dlp/yt-dlp/releases and put it there.";

        await DownloadLock.WaitAsync(ct);
        try
        {
            if (File.Exists(path)) return null;
            var url = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? WindowsDownloadUrl : LinuxDownloadUrl;
            Plugin.Log.LogInfo($"Downloading yt-dlp from {url} ...");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromMinutes(3);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("BigScreen-BigWalkMod/" + Plugin.Version);
            var bytes = await http.GetByteArrayAsync(url, ct);
            if (bytes.Length < 1_000_000) return "Downloaded yt-dlp looks too small to be real; refusing to use it.";

            var tmp = path + ".download";
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            File.Move(tmp, path, overwrite: true);
            // Note: under Proton/Wine the runtime reports Windows and uses yt-dlp.exe, which is
            // the only tested path. A native Linux binary would additionally need chmod +x.
            Plugin.Log.LogInfo($"yt-dlp saved to {path} ({bytes.Length / 1024 / 1024} MB).");
            return null;
        }
        catch (Exception e)
        {
            return "Could not download yt-dlp: " + e.Message;
        }
        finally
        {
            DownloadLock.Release();
        }
    }

    /// <summary>Resolves a page URL to a direct stream URL using the configured format selector.</summary>
    public static async Task<Result> ResolveAsync(string pageUrl, CancellationToken ct)
    {
        var result = new Result();
        if (!LooksLikeUrl(pageUrl))
        {
            result.Error = "That does not look like a URL (must start with http:// or https://).";
            return result;
        }

        var ensureError = await EnsureBinaryAsync(ct);
        if (ensureError != null)
        {
            result.Error = ensureError;
            return result;
        }

        var psi = new ProcessStartInfo
        {
            FileName = BinaryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--no-playlist");
        psi.ArgumentList.Add("--no-warnings");
        psi.ArgumentList.Add("--no-color");
        psi.ArgumentList.Add("--socket-timeout");
        psi.ArgumentList.Add("20");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(Plugin.FormatSelector.Value);
        // --print implies --simulate: nothing is downloaded. Three lines come back in this order.
        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add("%(title)s");
        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add("%(duration)s");
        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add("%(urls)s");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(pageUrl.Trim());

        try
        {
            using var proc = new Process { StartInfo = psi };
            proc.Start();
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                await proc.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                result.Error = "yt-dlp took too long (60 s) and was stopped.";
                return result;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (proc.ExitCode != 0)
            {
                var line = FirstUsefulLine(stderr) ?? $"exit code {proc.ExitCode}";
                result.Error = "yt-dlp failed: " + line +
                               (line.Contains("not available", StringComparison.OrdinalIgnoreCase)
                                   ? " (YouTube may have changed formats; try 'Update yt-dlp' in the panel)" : "");
                return result;
            }

            var lines = stdout.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 3)
            {
                result.Error = "yt-dlp returned an unexpected response: " + stdout.Trim();
                return result;
            }

            result.Title = lines[0].Trim();
            double.TryParse(lines[1].Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out result.Duration);

            // %(urls)s is newline-separated when the selector picks video+audio separately.
            // We only ask for muxed formats, so more than one URL means the selector was edited badly.
            var urlLines = Array.FindAll(lines[2..], l => LooksLikeUrl(l));
            if (urlLines.Length == 0)
            {
                result.Error = "yt-dlp did not return a stream URL.";
                return result;
            }
            if (urlLines.Length > 1)
            {
                result.Error = "The format selector picked separate video and audio streams. Unity's VideoPlayer " +
                               "needs a single muxed MP4; reset YtDlp.FormatSelector to its default.";
                return result;
            }

            result.DirectUrl = urlLines[0].Trim();
            result.Ok = true;
            return result;
        }
        catch (Exception e)
        {
            result.Error = "Could not run yt-dlp: " + e.Message;
            return result;
        }
    }

    /// <summary>Runs `yt-dlp -U` (self-update). YouTube breaks old versions regularly.</summary>
    public static async Task<string> UpdateAsync(CancellationToken ct)
    {
        var ensureError = await EnsureBinaryAsync(ct);
        if (ensureError != null) return ensureError;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = BinaryPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-U");
            using var proc = new Process { StartInfo = psi };
            proc.Start();
            var output = await proc.StandardOutput.ReadToEndAsync();
            var err = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync(ct);
            var summary = FirstUsefulLine(output) ?? FirstUsefulLine(err) ?? $"exit code {proc.ExitCode}";
            return summary;
        }
        catch (Exception e)
        {
            return "Update failed: " + e.Message;
        }
    }

    private static string FirstUsefulLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)) return line.Substring(6).Trim();
            return line;
        }
        return null;
    }
}
