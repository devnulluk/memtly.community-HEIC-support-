using System.Net;
using System.Text.Json;
using System.Threading.RateLimiting;

namespace Memtly.Community.Wedding;

public sealed class WeddingPublicOptions
{
    // Open, account-free uploads and cross-browser gallery sharing are separate.
    // Existing deployments default to per-browser viewing, even when their
    // persisted GalleryOpen switch is true. Sharing requires an explicit opt-in.
    public bool Enabled { get; set; } = true;
    public bool SharedGallery { get; set; } = false;
    public bool UploadsOpen { get; set; } = true;
    public bool GalleryOpen { get; set; } = true;
    public int UploadsPerBrowserPerMinute { get; set; } = 60;
    public int UploadsPerIpPerMinute { get; set; } = 240;
    public int SessionsPerIpPerMinute { get; set; } = 120;
    public int RequestsPerIpPerMinute { get; set; } = 3000;
    public void Validate()
    {
        if (UploadsPerBrowserPerMinute < 1 || UploadsPerIpPerMinute < 1 ||
            SessionsPerIpPerMinute < 1 || RequestsPerIpPerMinute < 1)
            throw new InvalidOperationException("Wedding rate limits must be positive.");
    }
}

public sealed record WeddingPublicSettings(bool UploadsOpen, bool GalleryOpen);
public sealed record WeddingVisibility(bool Hidden);

/// <summary>Small, atomic, persistent switches; no database/account migration.</summary>
public sealed class WeddingPublicState(WeddingStore store, WeddingPublicOptions options)
{
    private readonly object gate = new();
    private string Pathname => Path.Combine(store.Root, ".public-settings.json");
    public WeddingPublicSettings Read()
    {
        lock (gate)
        {
            try
            {
                var stored = File.Exists(Pathname)
                    ? JsonSerializer.Deserialize<WeddingPublicSettings>(File.ReadAllText(Pathname))
                    : new WeddingPublicSettings(true, true);
                return new(options.UploadsOpen && stored?.UploadsOpen == true,
                    options.GalleryOpen && stored?.GalleryOpen == true);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            { return new(false, false); } // Corrupt/unreadable settings fail closed.
        }
    }
    public WeddingPublicSettings Write(WeddingPublicSettings settings)
    {
        lock (gate)
        {
            var temporary = Pathname + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(settings));
                File.Move(temporary, Pathname, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return Read();
        }
    }
}

/// <summary>
/// In-memory, single-instance abuse limits. IP limits survive cookie replacement,
/// not a process restart; these are not a DDoS or distributed-bot defence.
/// RemoteIpAddress is used only after explicitly trusted proxy processing.
/// </summary>
public sealed class WeddingAbuseLimits : IDisposable
{
    private readonly PartitionedRateLimiter<string> reads, sessions, uploadIps, uploadOwners, activeOwners;
    public WeddingAbuseLimits(WeddingPublicOptions options)
    {
        reads = Window(options.RequestsPerIpPerMinute);
        sessions = Window(options.SessionsPerIpPerMinute);
        uploadIps = Window(options.UploadsPerIpPerMinute);
        uploadOwners = Window(options.UploadsPerBrowserPerMinute);
        activeOwners = PartitionedRateLimiter.Create<string, string>(key =>
            RateLimitPartition.GetConcurrencyLimiter(key, _ => new ConcurrencyLimiterOptions
                { PermitLimit = 1, QueueLimit = 0 }));
    }
    private static PartitionedRateLimiter<string> Window(int permits) =>
        PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetFixedWindowLimiter(key,
            _ => new FixedWindowRateLimiterOptions
            { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
    internal static string Ip(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress;
        if (ip == null) return "unknown";
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var bytes = ip.GetAddressBytes();
            Array.Clear(bytes, 8, 8); // One IPv6 /64 shares a budget; rotating addresses is not a bypass.
            return new IPAddress(bytes).ToString();
        }
        return ip.ToString();
    }
    public RateLimitLease Read(HttpContext context) => reads.AttemptAcquire(Ip(context));
    public RateLimitLease Session(HttpContext context) => sessions.AttemptAcquire(Ip(context));
    public RateLimitLease UploadIp(HttpContext context) => uploadIps.AttemptAcquire(Ip(context));
    public RateLimitLease UploadOwner(string owner) => uploadOwners.AttemptAcquire(owner);
    public RateLimitLease ActiveOwner(string owner) => activeOwners.AttemptAcquire(owner);
    public void Dispose()
    { reads.Dispose(); sessions.Dispose(); uploadIps.Dispose(); uploadOwners.Dispose(); activeOwners.Dispose(); }
}
