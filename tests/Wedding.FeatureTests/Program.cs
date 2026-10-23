using System.Net;
using System.Text.Json;
using Memtly.Community.Wedding;
using Microsoft.Extensions.Logging.Abstractions;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static void Denied(Action action, int status)
{
    try { action(); throw new Exception("Expected denial"); }
    catch (WeddingUploadException ex) { Check(ex.Status == status, "Unexpected denial status"); }
}
static async Task DeniedAsync(Func<Task> action, int status)
{
    try { await action(); throw new Exception("Expected denial"); }
    catch (WeddingUploadException ex) { Check(ex.Status == status, "Unexpected denial status"); }
}

// Capacity reservations must share the same disk budget, but written bytes must
// not be subtracted again after the filesystem already accounts for them.
long free = 1000;
var space = new WeddingDiskSpace("/tmp", 100, () => free);
using (var first = space.Reserve(600))
{
    Denied(() => space.Reserve(301), 507);
    using var second = space.Reserve(300);
    free -= 400; first.Written(400);
    first.Check(); second.Check();
    free -= 1; Denied(first.Check, 507); free += 1;
}
using (space.Reserve(500)) { }
Denied(() => space.Reserve(long.MaxValue), 507);

var root = Path.Combine(Path.GetTempPath(), "wedding-features-" + Guid.NewGuid().ToString("N"));
var options = new WeddingOptions
{
    Enabled = true, InviteKey = new string('x', 64), StoragePath = root, MinFreeBytes = 1048576,
    PublicUrl = "https://photos.example.test",
    Ntfy = new() { Enabled = true, Server = "https://ntfy.example.test", Topic = "private-test-topic", Token = "test-token" }
};
options.Validate(); // All byte/count quotas are zero (no configured quota).
options.MaxFileBytes = 8L * 1024 * 1024 * 1024; options.Validate(); options.MaxFileBytes = 0;
long available = 20L * 1024 * 1024;
var store = new WeddingStore(options, new WeddingDiskSpace(root, options.MinFreeBytes, () => available));
var video = new byte[] {0,0,0,24,102,116,121,112,109,112,52,50,0,0,0,0,109,112,52,50,105,115,111,109};
var requestId = Guid.NewGuid().ToString("N");
try
{
    await DeniedAsync(() => store.Save(new MemoryStream(video), "too-large.mov", "", "guest", Guid.NewGuid().ToString("N"), long.MaxValue, default), 507);
    Check(store.All().Count == 0, "Rejected admission created an upload");
    options.MaxFileBytes = 10;
    await DeniedAsync(() => store.Save(new MemoryStream(video), "limited.mov", "", "guest", requestId, video.Length, default), 413);
    options.MaxFileBytes = 0;
    await DeniedAsync(() => store.Save(new MemoryStream(video), "short.mov", "", "guest", requestId, video.Length + 1, default), 400);
    await DeniedAsync(() => store.Save(new MemoryStream(video), "unknown.mov", "", "guest", requestId, null, default), 411);
    var item = await store.Save(new MemoryStream(video), "memory.mov", "Keziah", "guest", requestId, video.Length, default);
    Check(item.Notification == "pending", "Missing durable notification intent");
    var duplicate = await store.Save(new MemoryStream(video), "memory.mov", "Keziah", "guest", requestId, video.Length, default);
    Check(duplicate.Id == item.Id && store.All().Count == 1, "Retry duplicated original");
    var handler = new FakeNtfy();
    var factory = new Factory(handler);
    var worker = new WeddingNtfyWorker(store, options, factory, NullLogger<WeddingNtfyWorker>.Instance);
    handler.Status = HttpStatusCode.ServiceUnavailable;
    Check(await worker.DeliverOne(default), "Pending notification not attempted");
    var pending = store.Find(item.Id)!;
    Check(pending.Notification == "pending" && pending.NotificationAttempts == 1 && pending.NotifyAfter > DateTimeOffset.UtcNow, "Failure not queued for backoff");
    Check(File.ReadAllBytes(store.Original(item)).SequenceEqual(video), "Notification failure altered original");
    Check(!await worker.DeliverOne(default) && handler.Calls == 1, "Retry ignored backoff");
    // A reconstructed store/worker still sees the pending message after a restart.
    store = new WeddingStore(options);
    store.Update(item.Id, i => i with { NotifyAfter = DateTimeOffset.UtcNow.AddSeconds(-1) });
    worker = new WeddingNtfyWorker(store, options, factory, NullLogger<WeddingNtfyWorker>.Instance);
    handler.Status = HttpStatusCode.OK;
    Check(await worker.DeliverOne(default), "Durable retry not delivered");
    Check(store.Find(item.Id)!.Notification == "sent", "Successful delivery not recorded");
    Check(!await worker.DeliverOne(default) && handler.Calls == 2, "Already-sent item delivered again");
    Check(handler.Url == "https://ntfy.example.test/" && handler.Auth == "Bearer test-token", "Wrong ntfy root or token header");
    using (var json = JsonDocument.Parse(handler.Body))
    {
        Check(json.RootElement.GetProperty("topic").GetString() == "private-test-topic", "Wrong topic");
        Check(json.RootElement.GetProperty("message").GetString()!.Contains("Keziah"), "Guest name missing");
        Check(json.RootElement.GetProperty("click").GetString() == "https://photos.example.test/Wedding/Admin", "Wrong private collection link");
        Check(!json.RootElement.TryGetProperty("attach", out _), "Media must not leave the server in notifications");
        Check(!handler.Body.Contains(options.InviteKey), "Invitation leaked");
    }
    options.Ntfy.IncludeGuestName = false;
    store.Update(item.Id, i => i with { Notification = "pending", NotifyAfter = null });
    await worker.DeliverOne(default);
    Check(!handler.Body.Contains("Keziah"), "Names-off setting ignored");
    options.Ntfy.Enabled = false;
    store.Update(item.Id, i => i with { Notification = "pending" });
    Check(!await worker.DeliverOne(default), "Disabled ntfy still sent");
    // An older manifest gets no retroactive notification when a new version starts.
    var legacy = JsonSerializer.Deserialize<WeddingItem>("{\"Id\":\"legacy\",\"Owner\":\"x\",\"RequestId\":\"x\",\"Filename\":\"x.mov\",\"Extension\":\".mov\",\"Name\":\"\",\"Bytes\":24,\"Sha256\":\"x\",\"UploadedUtc\":\"2026-01-01T00:00:00Z\"}");
    Check(legacy?.Notification == "disabled", "Old uploads must not trigger a notification flood");
    Console.WriteLine("PASS: unlimited/finite quotas, declared sizes, disk floor and concurrent reservations, unchanged originals, durable ntfy retry/backoff/restart, bearer authentication, private admin link, name privacy and legacy manifests.");
}
finally { if (Directory.Exists(root)) Directory.Delete(root, true); }

sealed class Factory(FakeNtfy handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}
sealed class FakeNtfy : HttpMessageHandler
{
    public HttpStatusCode Status = HttpStatusCode.OK;
    public int Calls;
    public string Url = "", Auth = "", Body = "";
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        Url = request.RequestUri!.AbsoluteUri;
        Auth = request.Headers.Authorization?.ToString() ?? "";
        Body = await request.Content!.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(Status) { Content = new StringContent("{}") };
    }
}
