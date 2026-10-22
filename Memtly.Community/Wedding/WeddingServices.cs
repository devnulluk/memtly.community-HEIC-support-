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
    public bool DevelopmentHttp { get; set; }
    public long MaxFileBytes { get; set; } = 500L * 1024 * 1024;
    public long MaxGuestBytes { get; set; } = 5L * 1024 * 1024 * 1024;
    public long MaxTotalBytes { get; set; } = 50L * 1024 * 1024 * 1024;
    public int MaxGuestFiles { get; set; } = 1000;
    public int CookieDays { get; set; } = 180;
    public void Validate()
    {
        if (!Enabled) return;
        if (InviteKey.Length < 32) throw new InvalidOperationException("Set Wedding__InviteKey to a random secret of at least 32 characters.");
        if (MaxFileBytes < 1 || MaxFileBytes > 500L * 1024 * 1024 || MaxGuestBytes < MaxFileBytes || MaxTotalBytes < MaxGuestBytes || MaxGuestFiles < 1 || CookieDays is < 1 or > 365)
            throw new InvalidOperationException("Invalid wedding upload limits.");
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
                guest.Id.Length == 64 && guest.Id.All(Uri.IsHexDigit) ? guest.Id : null;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException) { return null; }
    }
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
    string Preview = "pending", bool Reviewed = false)
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
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".webp", ".heic", ".heif", ".mp4", ".mov" };
    public WeddingStore(WeddingOptions options) { this.options = options; if (options.Enabled) Directory.CreateDirectory(Root); }
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
        var reservation = contentLength ?? options.MaxFileBytes;
        if (reservation < 1 || reservation > options.MaxFileBytes) throw new WeddingUploadException(413, "This file is larger than the upload limit.");
        var requestKey = owner + ":" + requestId;
        lock (admission)
        {
            var all = All();
            var previous = all.FirstOrDefault(i => i.Owner == owner && i.RequestId == requestId);
            if (previous != null)
            {
                if (previous.Filename != filename || (contentLength.HasValue && previous.Bytes != contentLength.Value))
                    throw new WeddingUploadException(409, "This upload ID has already been used for a different file.");
                return previous;
            }
            if (active.Count >= 4 || active.ContainsKey(requestKey)) throw new WeddingUploadException(429, "The photo postbox is busy. Please retry this file in a moment.");
            var own = all.Where(i => i.Owner == owner).ToList();
            if (own.Count + active.Values.Count(v => v.Owner == owner) >= options.MaxGuestFiles ||
                own.Sum(i => i.Bytes) + active.Values.Where(v => v.Owner == owner).Sum(v => v.Bytes) + reservation > options.MaxGuestBytes)
                throw new WeddingUploadException(413, "This browser has reached its upload allowance. Please contact Mark or Keziah.");
            if (all.Sum(i => i.Bytes) + active.Values.Sum(v => v.Bytes) + reservation > options.MaxTotalBytes)
                throw new WeddingUploadException(507, "Our photo postbox is full. Please let Mark or Keziah know.");
            active.Add(requestKey, (owner, reservation));
        }
        var id = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(Root, ".incoming-" + id);
        try
        {
            Directory.CreateDirectory(staging);
            var target = Path.Combine(staging, "original" + extension);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = 0;
            var buffer = new byte[64 * 1024];
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, FileOptions.Asynchronous))
            {
                while (true)
                {
                    var count = await input.ReadAsync(buffer, cancellation);
                    if (count == 0) break;
                    length += count;
                    if (length > reservation) throw new WeddingUploadException(413, "This file exceeded its upload allowance.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellation);
                }
                await output.FlushAsync(cancellation);
                output.Flush(flushToDisk: true);
            }
            if (length == 0 || (contentLength.HasValue && length != contentLength.Value)) throw new WeddingUploadException(400, "The upload was incomplete. Please retry it.");
            var header = new byte[64];
            using (var stream = File.OpenRead(target)) stream.Read(header, 0, header.Length);
            if (!SignatureMatches(extension, header)) throw new WeddingUploadException(415, "The file contents do not match its photo or video extension.");
            var item = new WeddingItem(id, owner, requestId, filename, extension, name, length,
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), DateTimeOffset.UtcNow,
                extension is ".mp4" or ".mov" ? "video" : "pending");
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
