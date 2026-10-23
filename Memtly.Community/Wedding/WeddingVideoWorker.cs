using System.Diagnostics;
using System.Text.Json;

namespace Memtly.Community.Wedding;

/// <summary>
/// One bounded video preview job at a time. Public playback never serves a master.
/// A preview is the first 120 seconds at <=1280 pixels/30fps; the full original is unchanged.
/// </summary>
public sealed class WeddingVideoWorker(WeddingStore store, WeddingOptions options,
    WeddingPublicOptions publicOptions, ILogger<WeddingVideoWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled || !publicOptions.Enabled) return;
        foreach (var item in store.All().Where(i => i.IsVideo && i.VideoPreview == "processing"))
            store.Update(item.Id, i => i with { VideoPreview = "pending" });
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var item = store.All().Where(i => i.IsVideo && i.VideoPreview == "pending")
                    .OrderBy(i => i.UploadedUtc).FirstOrDefault();
                if (item == null) { await Task.Delay(3000, stoppingToken); continue; }
                // Reserve enough for the 64 MiB output limit plus muxing/headroom.
                using var capacity = store.Space.Reserve(128L * 1024 * 1024);
                store.Update(item.Id, i => i with { VideoPreview = "processing" });
                var ok = await Convert(item, capacity, stoppingToken);
                store.Update(item.Id, i => i with { VideoPreview = ok ? "ready" : "unavailable" });
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning("Wedding video preview deferred ({ErrorType}); originals retained", ex.GetType().Name);
                try { await Task.Delay(30000, stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task<bool> Convert(WeddingItem item, WeddingDiskSpace.Reservation capacity, CancellationToken cancellation)
    {
        var source = store.Original(item);
        var temporary = Path.Combine(store.Folder(item.Id), ".video-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            var probe = new ProcessStartInfo("ffprobe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-v", "error", "-protocol_whitelist", "file,pipe", "-f", "mov", "-enable_drefs", "0", "-i", source,
                "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "json" }) probe.ArgumentList.Add(arg);
            var dimensions = await Run(probe, TimeSpan.FromSeconds(20), cancellation, captureOutput: true);
            if (dimensions == null) return false;
            using (var document = JsonDocument.Parse(dimensions))
            {
                var streams = document.RootElement.GetProperty("streams");
                if (streams.GetArrayLength() != 1) return false;
                var width = streams[0].GetProperty("width").GetInt32();
                var height = streams[0].GetProperty("height").GetInt32();
                if (width < 2 || height < 2 || (long)width * height > 40_000_000) return false;
            }
            var encode = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] {
                "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                "-protocol_whitelist", "file,pipe", "-f", "mov", "-enable_drefs", "0", "-threads", "2", "-i", source,
                "-t", "120", "-map", "0:v:0", "-map", "0:a:0?", "-map_metadata", "-1", "-map_chapters", "-1", "-sn", "-dn",
                "-vf", "scale=w='min(1280,iw)':h='min(1280,ih)':force_original_aspect_ratio=decrease:force_divisible_by=2,fps=30",
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "25", "-maxrate", "2500k", "-bufsize", "5000k",
                "-pix_fmt", "yuv420p", "-threads", "2", "-filter_threads", "1",
                "-c:a", "aac", "-b:a", "96k", "-ac", "2", "-movflags", "+faststart", "-fs", "67108864", temporary
            }) encode.ArgumentList.Add(arg);
            var result = await Run(encode, TimeSpan.FromMinutes(10), cancellation, captureOutput: false, capacity);
            if (result == null || !File.Exists(temporary) || new FileInfo(temporary).Length < 32) return false;
            capacity.Check();
            File.Move(temporary, Path.Combine(store.Folder(item.Id), "preview.video.mp4"), true);
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning("Video preview unavailable for {UploadId} ({ErrorType}); original unchanged", item.Id, ex.GetType().Name);
            return false;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task<string?> Run(ProcessStartInfo start, TimeSpan limit, CancellationToken cancellation,
        bool captureOutput, WeddingDiskSpace.Reservation? capacity = null)
    {
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start video decoder.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellation);
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, cancellation);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(limit);
        try
        {
            var exit = process.WaitForExitAsync(deadline.Token);
            while (!exit.IsCompleted)
            {
                await Task.WhenAny(exit, Task.Delay(2000, deadline.Token));
                deadline.Token.ThrowIfCancellationRequested();
                capacity?.Check();
            }
            await exit;
            await Task.WhenAll(stdout, stderr).WaitAsync(deadline.Token);
            return process.ExitCode == 0 ? (captureOutput ? await stdout : "ok") : null;
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            }
        }
    }
}
