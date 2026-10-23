using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Memtly.Community.Wedding;

public sealed class WeddingGuard(WeddingOptions options, WeddingPublicOptions publicOptions,
    WeddingSession session, WeddingAbuseLimits limits) : IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        if (!options.Enabled) { context.Result = new NotFoundResult(); return; }
        var http = context.HttpContext;
        if (!options.DevelopmentHttp && !http.Request.IsHttps)
        { context.Result = new BadRequestObjectResult(new { error = "Please open the secure HTTPS link." }); return; }
        var headers = http.Response.Headers;
        headers["Cache-Control"] = "private, no-store";
        headers["X-Robots-Tag"] = "noindex, nofollow, noarchive";
        headers["Referrer-Policy"] = "no-referrer";
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' blob:; media-src 'self' blob:; font-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
        if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method))
        {
            var origin = http.Request.Headers.Origin.ToString();
            var expected = string.IsNullOrWhiteSpace(options.PublicUrl)
                ? $"{http.Request.Scheme}://{http.Request.Host}" : options.PublicUrl;
            if (http.Request.Headers["Sec-Fetch-Site"] == "cross-site" ||
                (!string.IsNullOrEmpty(origin) && (!Uri.TryCreate(origin, UriKind.Absolute, out var actual) ||
                    actual.UserInfo != "" || actual.AbsolutePath != "/" || actual.Query != "" || actual.Fragment != "" ||
                    !actual.GetLeftPart(UriPartial.Authority).Equals(new Uri(expected).GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))))
            { context.Result = new ObjectResult(new { error = "Please upload from our wedding website." }) { StatusCode = 403 }; return; }
        }
        // Resource filters run before action bodies; upload limits are applied before reading bytes.
        if (!publicOptions.Enabled) { await next(); return; }
        var leases = new List<RateLimitLease>();
        try
        {
            leases.Add(limits.Read(http));
            var action = context.ActionDescriptor.RouteValues.GetValueOrDefault("action");
            if (action == "Session" && session.Owner(http) == null) leases.Add(limits.Session(http));
            if (action == "Upload")
            {
                leases.Add(limits.UploadIp(http));
                var owner = session.Owner(http);
                if (owner != null)
                {
                    leases.Add(limits.UploadOwner(owner));
                    leases.Add(limits.ActiveOwner(owner));
                }
            }
            var failed = leases.FirstOrDefault(l => !l.IsAcquired);
            if (failed != null)
            {
                var seconds = failed.TryGetMetadata(MetadataName.RetryAfter, out var retry)
                    ? Math.Clamp((int)Math.Ceiling(retry.TotalSeconds), 1, 60) : 5;
                headers["Retry-After"] = seconds.ToString(CultureInfo.InvariantCulture);
                context.Result = new ObjectResult(new { error = "The photo postbox is busy. Your upload will retry shortly.", retryAfter = seconds }) { StatusCode = 429 };
                return;
            }
            await next();
        }
        finally { foreach (var lease in leases) lease.Dispose(); }
    }
}

public sealed record WeddingEnter(string? Token);
public sealed record WeddingReview(bool Reviewed);

[Route("Wedding")]
[ServiceFilter(typeof(WeddingGuard))]
public sealed class WeddingController(WeddingStore store, WeddingSession session, WeddingOptions options,
    IWeddingAdmin administrator, IAntiforgery antiforgery, IWebHostEnvironment environment,
    WeddingPublicOptions publicOptions, WeddingPublicState publicState, ILogger<WeddingController> logger) : Controller
{
    private bool Admin => administrator.IsAdmin(User);
    private string? Owner => session.Owner(HttpContext);
    private IActionResult Page() => PhysicalFile(Path.Combine(environment.WebRootPath, "wedding", "index.html"), "text/html; charset=utf-8");

    [HttpGet("")]
    public IActionResult Index() => Page();

    [Authorize, HttpGet("Admin")]
    public IActionResult AdminPage() => Admin ? Page() : Forbid();

    [HttpGet("Session")]
    public IActionResult Session()
    {
        var invited = publicOptions.Enabled ? session.EnsurePublic(HttpContext) : Owner != null;
        var settings = publicState.Read();
        return Json(new
        {
            invited, admin = Admin, publicAccess = publicOptions.Enabled,
            uploadsOpen = settings.UploadsOpen, galleryOpen = settings.GalleryOpen,
            csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
            maxFileBytes = options.MaxFileBytes, videoPreviewSeconds = 120
        });
    }

    [HttpPost("Enter"), ValidateAntiForgeryToken, RequestSizeLimit(4096)]
    public IActionResult Enter([FromBody] WeddingEnter request) => session.Enter(HttpContext, request.Token)
        ? Json(new { success = true }) : StatusCode(403, new { error = "Please use the private link or QR code from Mark & Keziah." });

    [HttpGet("Items")]
    public IActionResult Items(int page = 0)
    {
        var owner = Owner;
        if (owner == null) return StatusCode(403, new { error = "Please open the wedding homepage and allow its essential cookie." });
        if (publicOptions.Enabled)
        {
            var items = publicState.Read().GalleryOpen ? store.All().Where(i => !i.Hidden) : Enumerable.Empty<WeddingItem>();
            return Listing(items, page, guest: true);
        }
        return Listing(store.All().Where(i => i.Owner == owner), page, guest: false);
    }

    [Authorize, HttpGet("AdminItems")]
    public IActionResult AdminItems(int page = 0) => Admin ? Listing(store.All(), page, guest: false) : Forbid();

    private IActionResult Listing(IEnumerable<WeddingItem> source, int page, bool guest)
    {
        var items = source.ToList();
        page = Math.Clamp(page, 0, 100000);
        return Json(new { total = items.Count, page, pages = (items.Count + 35) / 36,
            items = items.Skip(page * 36).Take(36).Select(i => guest ? GuestItem(i) : PrivateItem(i)) });
    }
    private object GuestItem(WeddingItem i) => new
    {
        id = i.Id, filename = i.IsVideo ? "Wedding video" : "Wedding photo",
        name = i.ShareName ? i.Name : "", uploadedUtc = i.UploadedUtc,
        preview = i.IsVideo ? i.VideoPreview : i.Preview, video = i.IsVideo
    };
    private object PrivateItem(WeddingItem i) => new { id = i.Id, filename = i.Filename, name = i.Name, bytes = i.Bytes,
        sha256 = i.Sha256, uploadedUtc = i.UploadedUtc,
        preview = i.IsVideo && publicOptions.Enabled ? i.VideoPreview : i.Preview,
        video = i.IsVideo, reviewed = i.Reviewed, hidden = i.Hidden };

    [HttpPost("Upload"), ValidateAntiForgeryToken, DisableRequestSizeLimit, DisableRequestTimeout]
    public async Task<IActionResult> Upload()
    {
        var owner = Owner;
        if (owner == null) return StatusCode(403, new { error = "Please reopen the wedding homepage and allow its essential cookie." });
        if (!publicState.Read().UploadsOpen) return StatusCode(403, new { error = "Uploads are now closed. Thank you for sharing your memories." });
        if (!string.Equals(Request.ContentType?.Split(';')[0], "application/octet-stream", StringComparison.OrdinalIgnoreCase))
            return StatusCode(415, new { error = "Please send the original file using the wedding uploader." });
        try
        {
            var filename = Uri.UnescapeDataString(Request.Headers["X-File-Name"].ToString());
            var name = Uri.UnescapeDataString(Request.Headers["X-Guest-Name"].ToString());
            var id = Request.Headers["X-Upload-ID"].ToString();
            var length = Request.ContentLength;
            var declared = Request.Headers["X-File-Size"].ToString();
            if (!string.IsNullOrEmpty(declared))
            {
                if (!long.TryParse(declared, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) || bytes <= 0 ||
                    (length.HasValue && length.Value != bytes))
                    return BadRequest(new { error = "The declared file size is invalid or does not match the request." });
                length = bytes;
            }
            var item = await store.Save(Request.Body, filename, name, owner, id, length, HttpContext.RequestAborted);
            // Only new explicit consent publishes a contributor name; legacy names stay private.
            if (publicOptions.Enabled && Request.Headers["X-Share-Name"] == "true" && !item.ShareName)
                item = store.Update(item.Id, i => i with { ShareName = true });
            return Json(new { success = true, item = PrivateItem(item) });
        }
        catch (WeddingUploadException ex) { if (ex.Status == 429) Response.Headers["Retry-After"] = "5"; return StatusCode(ex.Status, new { error = ex.Message }); }
        catch (UriFormatException) { return BadRequest(new { error = "Invalid upload filename." }); }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { return new EmptyResult(); }
        catch (IOException ex)
        {
            logger.LogError(ex, "Wedding upload storage error");
            return StatusCode(507, new { error = "We could not confirm this file. Please keep your original and let Mark or Keziah know." });
        }
    }

    private WeddingItem? Accessible(string id)
    {
        var admin = Admin;
        var owner = Owner;
        if (!admin && owner == null) return null;
        var item = store.Find(id);
        if (item == null) return null;
        if (admin) return item;
        if (publicOptions.Enabled) return publicState.Read().GalleryOpen && !item.Hidden ? item : null;
        return item.Owner == owner ? item : null;
    }

    [HttpGet("Preview/{id}")]
    public IActionResult Preview(string id)
    {
        var item = Accessible(id);
        if (item == null || item.IsVideo || item.Preview != "ready") return NotFound();
        var folder = store.Folder(id);
        var path = Path.Combine(folder, "preview.display.webp");
        if (!System.IO.File.Exists(path)) path = Path.Combine(folder, "preview.webp");
        return System.IO.File.Exists(path) ? PhysicalFile(path, "image/webp") : NotFound();
    }

    [HttpGet("Video/{id}")]
    public IActionResult Video(string id)
    {
        var item = Accessible(id);
        if (item == null || !item.IsVideo) return NotFound();
        if (!publicOptions.Enabled)
            return PhysicalFile(store.Original(item), item.Extension == ".mov" ? "video/quicktime" : "video/mp4", enableRangeProcessing: true);
        // NEVER fall back to the original in the public gallery, including on decoder failure.
        var preview = Path.Combine(store.Folder(id), "preview.video.mp4");
        return item.VideoPreview == "ready" && System.IO.File.Exists(preview)
            ? PhysicalFile(preview, "video/mp4", enableRangeProcessing: true) : NotFound();
    }

    [Authorize, HttpGet("Original/{id}")]
    public IActionResult Original(string id)
    {
        if (!Admin) return Forbid();
        var item = store.Find(id);
        return item != null ? PhysicalFile(store.Original(item), "application/octet-stream", item.Filename, enableRangeProcessing: true) : NotFound();
    }

    [Authorize, HttpGet("PublicSettings")]
    public IActionResult Settings() => Admin ? Json(publicState.Read()) : Forbid();

    [Authorize, HttpPost("PublicSettings"), ValidateAntiForgeryToken, RequestSizeLimit(4096)]
    public IActionResult Settings([FromBody] WeddingPublicSettings settings) => Admin ? Json(publicState.Write(settings)) : Forbid();

    [Authorize, HttpPost("Visibility/{id}"), ValidateAntiForgeryToken, RequestSizeLimit(4096)]
    public IActionResult Visibility(string id, [FromBody] WeddingVisibility visibility)
    {
        if (!Admin) return Forbid();
        if (store.Find(id) == null) return NotFound();
        return Json(PrivateItem(store.Update(id, i => i with { Hidden = visibility.Hidden })));
    }

    [Authorize, HttpPost("Review/{id}"), ValidateAntiForgeryToken, RequestSizeLimit(4096)]
    public IActionResult Review(string id, [FromBody] WeddingReview review)
    {
        if (!Admin) return Forbid();
        if (store.Find(id) == null) return NotFound();
        return Json(PrivateItem(store.Update(id, i => i with { Reviewed = review.Reviewed })));
    }

    [Authorize, HttpPost("Retry/{id}"), ValidateAntiForgeryToken, RequestSizeLimit(4096)]
    public IActionResult Retry(string id)
    {
        if (!Admin) return Forbid();
        var item = store.Find(id);
        if (item == null) return NotFound();
        if (item.IsVideo)
        {
            if (item.VideoPreview == "processing") return StatusCode(409);
            return Json(PrivateItem(store.Update(id, i => i with { VideoPreview = "pending" })));
        }
        if (item.Preview == "processing") return StatusCode(409);
        return Json(PrivateItem(store.Update(id, i => i with { Preview = "pending" })));
    }

    [Authorize, HttpGet("Export")]
    public async Task<IActionResult> Export()
    {
        if (!Admin) return Forbid();
        FileStream? stream = null;
        try
        {
            var items = store.All();
            var required = items.Sum(i => (decimal)i.Bytes) + (decimal)(items.Count + 1) * 65536;
            if (required > long.MaxValue) throw new WeddingUploadException(507, "This collection is too large for a single ZIP export.");
            using var disk = store.Space.Reserve((long)required);
            var directory = Path.Combine(store.Root, ".exports");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".zip");
            stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                var buffer = new byte[65536];
                long sinceCheck = 0;
                foreach (var item in items)
                {
                    var entry = zip.CreateEntry(item.Id + "/" + item.Filename, CompressionLevel.NoCompression);
                    using var output = entry.Open();
                    await using var original = System.IO.File.OpenRead(store.Original(item));
                    int count;
                    while ((count = await original.ReadAsync(buffer, HttpContext.RequestAborted)) > 0)
                    {
                        if (sinceCheck >= 8L * 1024 * 1024) { disk.Check(); sinceCheck = 0; }
                        await output.WriteAsync(buffer.AsMemory(0, count), HttpContext.RequestAborted);
                        disk.Written(count);
                        sinceCheck += count;
                    }
                }
                var manifest = zip.CreateEntry("upload-index.json");
                using var metadata = manifest.Open();
                await JsonSerializer.SerializeAsync(metadata, items.Select(PrivateItem), cancellationToken: HttpContext.RequestAborted);
            }
            disk.Check();
            stream.Position = 0;
            return File(stream, "application/zip", "Mark-and-Keziah-wedding-originals.zip");
        }
        catch (WeddingUploadException ex)
        {
            if (stream != null) await stream.DisposeAsync();
            return StatusCode(ex.Status, new { error = ex.Message });
        }
        catch { if (stream != null) await stream.DisposeAsync(); throw; }
    }
}
