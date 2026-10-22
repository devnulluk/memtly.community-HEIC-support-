// TEST-ONLY HOST. This project is never published into the production image.
using System.Security.Claims;
using System.Text.Encodings.Web;
using Memtly.Community.Wedding;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
var root = Environment.GetEnvironmentVariable("TEST_ROOT") ?? Path.Combine(Path.GetTempPath(), "wedding-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(root, "keys"));
builder.WebHost.UseWebRoot(Environment.GetEnvironmentVariable("WEDDING_WEBROOT") ?? Path.GetFullPath("Memtly.Community/wwwroot"));
var options = new WeddingOptions { Enabled = true, InviteKey = "test-only-invitation-not-for-deployment-12345", StoragePath = Path.Combine(root, "media"), DevelopmentHttp = true, MaxFileBytes = 1048576 };
options.Validate();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<WeddingSession>();
builder.Services.AddSingleton<WeddingStore>();
builder.Services.AddScoped<WeddingGuard>();
builder.Services.AddScoped<IWeddingAdmin, TestAdmin>();
builder.Services.AddScoped<IWeddingPreview, FailedPreview>();
builder.Services.AddHostedService<WeddingPreviewWorker>();
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(root, "keys")));
builder.Services.AddAntiforgery(o => { o.HeaderName = "X-Wedding-CSRF"; o.Cookie.IsEssential = true; });
builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthentication>("Test", _ => { });
builder.Services.AddAuthorization();
builder.Services.AddControllers();
var app = builder.Build();
app.UseAuthentication(); app.UseAuthorization(); app.MapControllers();
app.Run();

sealed class TestAdmin : IWeddingAdmin { public bool IsAdmin(ClaimsPrincipal user) => user.IsInRole("Admin"); }
// Intentionally fail conversion: test that failed previews never lose the original.
sealed class FailedPreview : IWeddingPreview { public Task<bool> Write(string source, string thumbnail) => Task.FromResult(false); }
sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers["X-Test-Admin"] != "test-host-only") return Task.FromResult(AuthenticateResult.NoResult());
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "admin"), new Claim(ClaimTypes.Role, "Admin") }, Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}
