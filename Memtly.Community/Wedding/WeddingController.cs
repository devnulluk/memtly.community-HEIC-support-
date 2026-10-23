using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Memtly.Community.Wedding;

public sealed class WeddingGuard(WeddingOptions options) : IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        if (!options.Enabled) { context.Result = new NotFoundResult(); return; }
        if (!options.DevelopmentHttp && !context.HttpContext.Request.IsHttps)
        { context.Result = new BadRequestObjectResult(new { error = "Please open the secure HTTPS link." }); return; }
        var headers = context.HttpContext.Response.Headers;
        headers["Cache-Control"] = "private, no-store";
        headers["X-Robots-Tag"] = "noindex, nofollow, noarchive";
        headers["Referrer-Policy"] = "no-referrer";
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' blob:; media-src 'self' blob:; font-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
        await next();
    }
}

public sealed record WeddingEnter(string? Token);
public sealed record WeddingReview(bool Reviewed);

[Route("Wedding")]
[ServiceFilter(typeof(WeddingGuard))]
public sealed class WeddingController(WeddingStore store, WeddingSession session, WeddingOptions options,
    IWeddingAdmin administrator, IAntiforgery antiforgery, IWebHostEnvironment environment, ILogger<WeddingController> logger) : Controller
{
    private bool Admin => administrator.IsAdmin(User);
    private string? Owner => session.Owner(HttpContext);
    private IActionResult Page() => PhysicalFile(Path.Combine(environment.WebRootPath, "wedding", "index.html"), "text/html; charset=utf-8");

    [HttpGet("")]
    public IActionResult Index() => Page();

    [Authorize, HttpGet("Admin")]
    public IActionResult AdminPage() => Admin ? Page() : Forbid();

    [HttpGet("Session")]
    public IActionResult Session() => Json(new
    {
        invited = Owner != null, admin = Admin,
        csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
        maxFileBytes = options.MaxFileBytes
    });

    [HttpPost("Enter"), ValidateAntiForgeryToken, RequestSizeLimit(4096)]
    public IActionResult Enter([FromBody] WeddingEnter request) => session.Enter(HttpContext, request.Token)
        ? Json(new { success = true }) : StatusCode(403, new { error = "Please use the private link or QR code from Mark & Keziah." });

    [HttpGet("Items")]
    public IActionResult Items(int page = 0)
    {
        var owner = Owner;
        if (owner == null) return StatusCode(403, new { error = "Please scan your invitation QR code first." });
        return Listing(store.All().Where(i => i.Owner == owner), page);
    }

    [Authorize, HttpGet("AdminItems")]
    public IActionResult AdminItems(int page = 0) => Admin ? Listing(store.All(), page) : Forbid();

    private IActionResult Listing(IEnumerable<WeddingItem> source, int page)
    {
        var items = source.ToList();
        page = Math.Clamp(page, 0, 100000);
        return Json(new { total = items.Count, page, pages = (items.Count + 35) / 36,
            items = items.Skip(page * 36).Take(36).Select(PublicItem) });
    }
    private object PublicItem(WeddingItem i) => new { id = i.Id, filename = i.Filename, name = i.Name, bytes = i.Bytes,
        sha256 = i.Sha256, uploadedUtc = i.UploadedUtc, preview = i.Preview, video = i.IsVideo, reviewed = i.Reviewed };

    // Override Kestrel only for this streamed action, not other Memtly endpoints.
    [HttpPost("Upload"), ValidateAntiForgeryToken, DisableRequestSizeLimit, DisableRequestTimeout]
    public async Task<IActionResult> Upload()
    {
        var owner = Owner;
        if (owner == null) return StatusCode(403, new { error = "Please open the private invitation link again." });
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
            return Json(new { success = true, item = PublicItem(item) });
        }
        catch (WeddingUploadException ex) { if (ex.Status == 429) Response.Headers["Retry-After"] = "5"; return StatusCode(ex.Status, new { error = ex.Message }); }
        catch (UriFormatException) { return BadRequest(new { error = "Invalid upload filename." }); }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { return new EmptyResult(); }
        catch (IOException ex)
        {
            logger.LogError(ex, "Wedding upload storage error");
            return StatusCode(507, new { error = "We could not save this file. Please keep it and let Mark or Keziah know." });
        }
    }

    private WeddingItem? Accessible(string id)
    {
        if (!Admin && Owner == null) return null;
        var item = store.Find(id);
        return item != null && (Admin || item.Owner == Owner) ? item : null;
    }

    [HttpGet("Preview/{id}")]
    public IActionResult Preview(string id)
    {
        var item = Accessible(id);
        if (item == null || item.Preview != "ready") return NotFound();
        var folder = store.Folder(id);
        var path = Path.Combine(folder, "preview.display.webp");
        if (!System.IO.File.Exists(path)) path = Path.Combine(folder, "preview.webp");
        return System.IO.File.Exists(path) ? PhysicalFile(path, "image/webp") : NotFound();
    }

    [HttpGet("Video/{id}")]
    public IActionResult Video(string id)
    {
        var item = Accessible(id);
        return item != null && item.IsVideo
            ? PhysicalFile(store.Original(item), item.Extension == ".mov" ? "video/quicktime" : "video/mp4", enableRangeProcessing: true)
            : NotFound();
    }

    [Authorize, HttpGet("Original/{id}")]
    public IActionResult Original(string id)
    {
        if (!Admin) return Forbid();
        var item = store.Find(id);
        return item != null ? PhysicalFile(store.Original(item), "application/octet-stream", item.Filename, enableRangeProcessing: true) : NotFound();
    }

    [Authorize, HttpPost("Review/{id}"), ValidateAntiForgeryToken, RequestSizeLimit(4096)]
    public IActionResult Review(string id, [FromBody] WeddingReview review)
    {
        if (!Admin) return Forbid();
        if (store.Find(id) == null) return NotFound();
        return Json(PublicItem(store.Update(id, i => i with { Reviewed = review.Reviewed })));
    }

    [Authorize, HttpPost("Retry/{id}"), ValidateAntiForgeryToken, RequestSizeLimit(4096)]
    public IActionResult Retry(string id)
    {
        if (!Admin) return Forbid();
        var item = store.Find(id);
        if (item == null || item.IsVideo) return NotFound();
        if (item.Preview == "processing") return StatusCode(409);
        return Json(PublicItem(store.Update(id, i => i with { Preview = "pending" })));
    }

    [Authorize, HttpGet("Export")]
    public async Task<IActionResult> Export()
    {
        if (!Admin) return Forbid();
        FileStream? stream = null;
        try
        {
            var items = store.All();
            // Reserve temporary ZIP capacity as well as originals; large libraries
            // must not bypass the free-space floor through an administrator export.
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
                await JsonSerializer.SerializeAsync(metadata, items.Select(PublicItem), cancellationToken: HttpContext.RequestAborted);
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
