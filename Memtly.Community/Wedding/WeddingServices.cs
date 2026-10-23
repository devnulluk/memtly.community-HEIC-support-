using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Memtly.Community.Wedding;

public sealed class WeddingOptions
{
    public bool Enabled { get; set; }
    public string InviteKey { get; set; } = "";
    public string StoragePath { get; set; } = "/data/wedding";
    public string TrustedProxy { get; set; } = "";
    public string PublicUrl { get; set; } = "";
    public bool DevelopmentHttp { get; set; }
    // Zero disables the corresponding application quota, not disk-space protection.
    public long MaxFileBytes { get; set; }
    public long MaxGuestBytes { get; set; }
    public long MaxTotalBytes { get; set; }
    public int MaxGuestFiles { get; set; }
    public long MinFreeBytes { get; set; } = 10L * 1024 * 1024 * 1024;
    public int CookieDays { get; set; } = 180;
    public WeddingNtfyOptions Ntfy { get; set; } = new();
    public void Validate()
    {
        if (!Enabled) return;
        if (InviteKey.Length < 32) throw new InvalidOperationException("Set Wedding__InviteKey to a random secret of at least 32 characters. This remains a server-side identity namespace in public mode.");
        if (MaxFileBytes < 0 || MaxGuestBytes < 0 || MaxTotalBytes < 0 || MaxGuestFiles < 0 ||
            MinFreeBytes < 1024 * 1024 || CookieDays is < 1 or > 365)
            throw new InvalidOperationException("Wedding quotas must be non-negative (0 = no quota); retain at least 1 MiB of free-space reserve.");
        if (!string.IsNullOrEmpty(PublicUrl) && !WeddingNtfyOptions.ValidServer(PublicUrl, DevelopmentHttp))
            throw new InvalidOperationException("Wedding:PublicUrl must be an HTTPS base URL without credentials, query or fragment.");
        Ntfy.Validate(DevelopmentHttp);
    }
}

public interface IWeddingAdmin { bool IsAdmin(ClaimsPrincipal user); }
public interface IWeddingPreview { Task<bool> Write(string source, string thumbnail); }
public sealed record GuestCredential(string Id, string Invitation, DateTimeOffset Expires);

public sealed class WeddingSession
{
    private readonly IDataProtector protector;
    private readonly WeddingOptions options;
    private string CookieName => options.DevelopmentHttp ? "NewlandBrownGuestDev" : "__Host-NewlandBrownGuest";
    private string Invitation => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(options.InviteKey)));
    public WeddingSession(IDataProtectionProvider provider, WeddingOptions options)
    {
        protector = provider.CreateProtector("NewlandBrown.Wedding.Guest.v1");
        this.options = options;
    }
    public string? Owner(HttpContext context)
    {
        try
        {
            var cookie = context.Request.Cookies[CookieName];
            if (cookie == null || cookie.Length > 2048) return null;
            var guest = JsonSerializer.Deserialize<GuestCredential>(protector.Unprotect(cookie));
            return guest != null && guest.Expires > DateTimeOffset.UtcNow && guest.Invitation == Invitation &&
                guest.Id?.Length == 64 && guest.Id.All(Uri.IsHexDigit) ? guest.Id : null;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException) { return null; }
    }
    // Issued server-side; callers never disclose the invitation secret in public mode.
    public bool EnsurePublic(HttpContext context) => Owner(context) != null || Enter(context, options.InviteKey);
    public bool Enter(HttpContext context, string? token)
    {
        if (token == null || token.Length > 512 || !CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(token)), SHA256.HashData(Encoding.UTF8.GetBytes(options.InviteKey)))) return false;
        var id = Owner(context) ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var expiry = DateTimeOffset.UtcNow.AddDays(options.CookieDays);
        context.Response.Cookies.Append(CookieName,
            protector.Protect(JsonSerializer.Serialize(new GuestCredential(id, Invitation, expiry))),
            new CookieOptions { HttpOnly = true, Secure = !options.DevelopmentHttp, IsEssential = true,
                SameSite = SameSiteMode.Lax, Path = "/", Expires = expiry });
        return true;
    }
}

public sealed record WeddingItem(
    string Id, string Owner, string RequestId, string Filename, string Extension,
    string Name, long Bytes, string Sha256, DateTimeOffset UploadedUtc,
    string Preview = "pending", bool Reviewed = false,
    string Notification = "disabled", int NotificationAttempts = 0,
    DateTimeOffset? NotifyAfter = null, DateTimeOffset? NotifiedUtc = null,
    string VideoPreview = "pending", bool Hidden = false, bool ShareName = false)
{
    public bool IsVideo => Extension is ".mp4" or ".mov";
}

public sealed class WeddingUploadException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>Single-instance, private, append-only originals with atomic per-upload manifests.</summary>
public sealed class WeddingStore
{
    private readonly WeddingOptions options;
    private readonly object admission = new();
    private readonly object manifests = new();
    private readonly Dictionary<string, (string Owner, long Bytes)> active = new();
    public string Root => Path.GetFullPath(options.StoragePath);
    public WeddingDiskSpace Space { get; }
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".webp", ".heic", ".heif", ".mp4", ".mov" };
    public WeddingStore(WeddingOptions options) : this(options, new WeddingDiskSpace(options.StoragePath, options.MinFreeBytes)) { }
    internal WeddingStore(WeddingOptions options, WeddingDiskSpace space)
    {
        this.options = options;
        Space = space;
        if (options.Enabled) Directory.CreateDirectory(Root);
    }
    public static bool ValidId(string id) => Guid.TryParseExact(id, "N", out _);
    public string Folder(string id) => ValidId(id) ? Path.Combine(Root, id) : throw new ArgumentException("Invalid upload ID.");
    public string Original(WeddingItem item) => Path.Combine(Folder(item.Id), "original" + item.Extension);
    public WeddingItem? Find(string id)
    {
        if (!ValidId(id)) return null;
        var path = Path.Combine(Folder(id), "upload.json");
        if (!File.Exists(path)) return null;
        var item = JsonSerializer.Deserialize<WeddingItem>(File.ReadAllText(path));
        if (item == null || item.Id != id || !Extensions.Contains(item.Extension)) throw new InvalidDataException("Invalid wedding manifest.");
        return item;
    }
    public List<WeddingItem> All() => !Directory.Exists(Root) ? new() : Directory.EnumerateDirectories(Root)
        .Select(Path.GetFileName).Where(id => id != null && ValidId(id))
        .Select(id => Find(id!)).OfType<WeddingItem>().OrderByDescending(i => i.UploadedUtc).ToList();

    public WeddingItem Update(string id, Func<WeddingItem, WeddingItem> change)
    {
        lock (manifests)
        {
            var item = change(Find(id) ?? throw new FileNotFoundException());
            var path = Path.Combine(Folder(id), "upload.json");
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temp, JsonSerializer.Serialize(item)); File.Move(temp, path, true); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return item;
        }
    }

    public async Task<WeddingItem> Save(Stream input, string filename, string name, string owner, string requestId, long? contentLength, CancellationToken cancellation)
    {
        filename = Path.GetFileName(filename.Replace('\\', '/'));
        filename = new string(filename.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (filename.Length is < 1 or > 180) throw new WeddingUploadException(400, "That filename is too long.");
        var extension = Path.GetExtension(filename).ToLowerInvariant();
        if (!Extensions.Contains(extension)) throw new WeddingUploadException(415, "Please choose JPEG, PNG, WebP, HEIC, HEIF, MP4 or MOV files.");
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length > 80) throw new WeddingUploadException(400, "Please keep your name to 80 characters.");
        if (!ValidId(requestId)) throw new WeddingUploadException(400, "Missing upload request ID.");
        if (contentLength is not > 0) throw new WeddingUploadException(411, "Please provide the file size before uploading.");
        var reservation = contentLength.Value;
        if (options.MaxFileBytes > 0 && reservation > options.MaxFileBytes)
            throw new WeddingUploadException(413, "This file is larger than the configured upload limit.");
        var requestKey = owner + ":" + requestId;
        WeddingDiskSpace.Reservation disk;
        lock (admission)
        {
            var all = All();
            var previous = all.FirstOrDefault(i => i.Owner == owner && i.RequestId == requestId);
            if (previous != null)
            {
                if (previous.Filename != filename || previous.Bytes != reservation)
                    throw new WeddingUploadException(409, "This upload ID has already been used for a different file.");
                return previous;
            }
            if (active.Count >= 4 || active.ContainsKey(requestKey)) throw new WeddingUploadException(429, "The photo postbox is busy. Please retry this file in a moment.");
            var own = all.Where(i => i.Owner == owner).ToList();
            if ((options.MaxGuestFiles > 0 && own.Count + active.Values.Count(v => v.Owner == owner) >= options.MaxGuestFiles) ||
                (options.MaxGuestBytes > 0 && own.Sum(i => (decimal)i.Bytes) + active.Values.Where(v => v.Owner == owner).Sum(v => (decimal)v.Bytes) + reservation > options.MaxGuestBytes))
                throw new WeddingUploadException(413, "This browser has reached its configured upload allowance. Please contact Mark or Keziah.");
            if (options.MaxTotalBytes > 0 && all.Sum(i => (decimal)i.Bytes) + active.Values.Sum(v => (decimal)v.Bytes) + reservation > options.MaxTotalBytes)
                throw new WeddingUploadException(507, "Our photo postbox has reached its configured allowance. Please let Mark or Keziah know.");
            disk = Space.Reserve(reservation);
            active.Add(requestKey, (owner, reservation));
        }
        using var diskReservation = disk;
        var id = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(Root, ".incoming-" + id);
        try
        {
            Directory.CreateDirectory(staging);
            var target = Path.Combine(staging, "original" + extension);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            long length = 0, sinceSpaceCheck = 0;
            var buffer = new byte[64 * 1024];
            var header = new byte[64];
            var headerCount = 0;
            var headerValidated = false;
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, FileOptions.Asynchronous))
            {
                while (true)
                {
                    readTimeout.CancelAfter(TimeSpan.FromMinutes(2));
                    int count;
                    try { count = await input.ReadAsync(buffer, readTimeout.Token); }
                    catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                    { throw new WeddingUploadException(408, "The connection stopped sending data. Please retry this file."); }
                    finally { readTimeout.CancelAfter(Timeout.InfiniteTimeSpan); }
                    if (count == 0) break;
                    if (count > reservation - length) throw new WeddingUploadException(413, "This file exceeded its declared size.");
                    if (!headerValidated)
                    {
                        var take = Math.Min(header.Length - headerCount, count);
                        buffer.AsSpan(0, take).CopyTo(header.AsSpan(headerCount));
                        headerCount += take;
                        if (headerCount == header.Length || length + count == reservation)
                        {
                            if (!SignatureMatches(extension, header))
                                throw new WeddingUploadException(415, "The file contents do not match its photo or video extension.");
                            headerValidated = true;
                        }
                    }
                    if (sinceSpaceCheck >= 8L * 1024 * 1024) { disk.Check(); sinceSpaceCheck = 0; }
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellation);
                    disk.Written(count);
                    length += count;
                    sinceSpaceCheck += count;
                }
                await output.FlushAsync(cancellation);
                output.Flush(flushToDisk: true);
            }
            if (length != reservation) throw new WeddingUploadException(400, "The upload was incomplete. Please retry it.");
            disk.Check();
            if (!headerValidated) throw new WeddingUploadException(415, "Invalid media header.");
            var item = new WeddingItem(id, owner, requestId, filename, extension, name, length,
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), DateTimeOffset.UtcNow,
                Preview: extension is ".mp4" or ".mov" ? "video" : "pending",
                Notification: options.Ntfy.Enabled ? "pending" : "disabled");
            await File.WriteAllTextAsync(Path.Combine(staging, "upload.json"), JsonSerializer.Serialize(item), cancellation);
            Directory.Move(staging, Folder(id));
            return item;
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            finally { lock (admission) active.Remove(requestKey); }
        }
    }

    private static bool SignatureMatches(string extension, byte[] h)
    {
        var tag = Encoding.ASCII.GetString(h, 4, 4);
        return extension switch
        {
            ".jpg" or ".jpeg" => h[0] == 255 && h[1] == 216 && h[2] == 255,
            ".png" => h.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}),
            ".webp" => Encoding.ASCII.GetString(h, 0, 4) == "RIFF" && Encoding.ASCII.GetString(h, 8, 4) == "WEBP",
            ".heic" or ".heif" => tag == "ftyp" && new[] { "heic", "heix", "hevc", "hevx", "mif1", "msf1" }.Any(b => Encoding.ASCII.GetString(h).Contains(b, StringComparison.Ordinal)),
            ".mp4" or ".mov" => tag == "ftyp",
            _ => false
        };
    }
}

public sealed class WeddingPreviewWorker(IServiceScopeFactory scopes, WeddingStore store, WeddingOptions options, ILogger<WeddingPreviewWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        foreach (var item in store.All().Where(i => i.Preview == "processing")) store.Update(item.Id, i => i with { Preview = "pending" });
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var item = store.All().FirstOrDefault(i => i.Preview == "pending");
                if (item == null) { await Task.Delay(2000, stoppingToken); continue; }
                store.Update(item.Id, i => i with { Preview = "processing" });
                var ok = false;
                try
                {
                    using var scope = scopes.CreateScope();
                    ok = await scope.ServiceProvider.GetRequiredService<IWeddingPreview>().Write(store.Original(item), Path.Combine(store.Folder(item.Id), "preview.webp"));
                }
                catch (Exception ex) { logger.LogWarning(ex, "Wedding preview failed for {UploadId}; original retained", item.Id); }
                store.Update(item.Id, i => i with { Preview = ok ? "ready" : "unavailable" });
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Wedding preview queue error");
                try { await Task.Delay(10000, stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
    }
}
