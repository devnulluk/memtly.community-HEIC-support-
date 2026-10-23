using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;

namespace Memtly.Community.Wedding;

public sealed class WeddingNtfyOptions
{
    public bool Enabled { get; set; }
    public string Server { get; set; } = "";
    public string Topic { get; set; } = "";
    public string Token { get; set; } = "";
    public bool IncludeGuestName { get; set; } = true;
    internal static bool ValidServer(string value, bool developmentHttp) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == "https" || (developmentHttp && uri.Scheme == "http")) &&
        !string.IsNullOrWhiteSpace(uri.Host) && uri.UserInfo == "" && uri.Query == "" && uri.Fragment == "";
    public void Validate(bool developmentHttp)
    {
        if (!Enabled) return;
        if (!ValidServer(Server, developmentHttp) || !Regex.IsMatch(Topic, "\\A[A-Za-z0-9_-]{1,64}\\z") || Token.Any(char.IsControl))
            throw new InvalidOperationException("Set a valid HTTPS ntfy server root, topic (letters/digits/_/-) and optional access token. Do not include credentials in the URL.");
    }
}

public static class WeddingNtfyRegistration
{
    public static IServiceCollection AddWeddingNtfy(this IServiceCollection services)
    {
        services.AddHttpClient("WeddingNtfy", client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false
            });
        services.AddHostedService<WeddingNtfyWorker>();
        return services;
    }
}

/// <summary>
/// Durable outbox on committed upload manifests. Delivery is at-least-once: a server
/// accepting a notification and losing its response can cause a duplicate after retry.
/// Upload requests never wait for, or roll back because of, notification delivery.
/// </summary>
public sealed class WeddingNtfyWorker(WeddingStore store, WeddingOptions options,
    IHttpClientFactory clients, ILogger<WeddingNtfyWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled || !options.Ntfy.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var worked = await DeliverOne(stoppingToken);
                await Task.Delay(worked ? 1000 : 5000, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Never log endpoint credentials, request bodies or guest names.
                logger.LogWarning("Wedding notification outbox error ({ErrorType}); originals remain stored", ex.GetType().Name);
                try { await Task.Delay(30000, stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
    }

    internal async Task<bool> DeliverOne(CancellationToken cancellation)
    {
        if (!options.Enabled || !options.Ntfy.Enabled) return false;
        var item = store.All().Where(i => i.Notification == "pending" &&
                (i.NotifyAfter == null || i.NotifyAfter <= DateTimeOffset.UtcNow))
            .OrderBy(i => i.UploadedUtc).FirstOrDefault();
        if (item == null) return false;
        var attempts = Math.Min(30, item.NotificationAttempts + 1);
        var delay = TimeSpan.FromSeconds(Math.Min(3600, 30 * Math.Pow(2, Math.Min(attempts - 1, 7))));
        var sender = options.Ntfy.IncludeGuestName && !string.IsNullOrWhiteSpace(item.Name) ? item.Name : "A guest";
        var size = item.Bytes >= 1024L * 1024 * 1024
            ? ((double)item.Bytes / (1024L * 1024 * 1024)).ToString("0.00", CultureInfo.InvariantCulture) + " GiB"
            : ((double)item.Bytes / (1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MiB";
        var payload = new Dictionary<string, object>
        {
            ["topic"] = options.Ntfy.Topic,
            ["title"] = "New wedding memory",
            ["message"] = $"{sender} uploaded a {(item.IsVideo ? "video" : "photo")} ({size}).\n{item.Filename}\nOriginal safely stored. Reference: {item.Id[..8]}",
            ["tags"] = new[] { item.IsVideo ? "movie_camera" : "camera", "white_heart" },
            ["priority"] = 3
        };
        if (!string.IsNullOrWhiteSpace(options.PublicUrl))
            payload["click"] = options.PublicUrl.TrimEnd('/') + "/Wedding/Admin";
        var accepted = false;
        try
        {
            using var client = clients.CreateClient("WeddingNtfy");
            // ntfy JSON publishing uses the SERVER ROOT, not /topic.
            using var request = new HttpRequestMessage(HttpMethod.Post, options.Ntfy.Server.TrimEnd('/') + "/")
            { Content = JsonContent.Create(payload) };
            if (!string.IsNullOrWhiteSpace(options.Ntfy.Token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Ntfy.Token);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            accepted = response.IsSuccessStatusCode;
            if (!accepted)
            {
                var retry = response.Headers.RetryAfter?.Delta ??
                    (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow);
                if (retry.HasValue && retry.Value > delay) delay = retry.Value;
                logger.LogWarning("Wedding notification for {UploadId} returned HTTP {Status}; queued for retry", item.Id, (int)response.StatusCode);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            logger.LogWarning("Wedding notification for {UploadId} was not confirmed ({ErrorType}); queued for retry", item.Id, ex.GetType().Name);
        }
        if (accepted)
            store.Update(item.Id, i => i with { Notification = "sent", NotificationAttempts = attempts, NotifyAfter = null, NotifiedUtc = DateTimeOffset.UtcNow });
        else
            store.Update(item.Id, i => i with { NotificationAttempts = attempts, NotifyAfter = DateTimeOffset.UtcNow.Add(delay) });
        return true;
    }
}
