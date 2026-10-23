using System.Net;
using System.Security.Claims;
using Memtly.Core.Helpers;
using Memtly.Core.Enums;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;

namespace Memtly.Community.Wedding;

public sealed class MemtlyWeddingAdmin(IIdentityHelper identity) : IWeddingAdmin
{
    public bool IsAdmin(ClaimsPrincipal user) => identity.IsValid(user) && identity.GetUserLevel(user) == UserLevel.Admin;
}

public sealed class MemtlyWeddingPreview(IImageHelper images) : IWeddingPreview
{
    public Task<bool> Write(string source, string thumbnail) => images.GenerateThumbnail(source, thumbnail, 1600);
}

public static class WeddingIntegration
{
    public static IServiceCollection AddWeddingPortal(this IServiceCollection services, IConfiguration config)
    {
        var options = config.GetSection("Wedding").Get<WeddingOptions>() ?? new();
        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton<WeddingSession>();
        services.AddSingleton<WeddingStore>();
        services.AddScoped<IWeddingAdmin, MemtlyWeddingAdmin>();
        services.AddScoped<IWeddingPreview, MemtlyWeddingPreview>();
        services.AddScoped<WeddingGuard>();
        services.AddHostedService<WeddingPreviewWorker>();
        services.AddWeddingNtfy();
        services.AddAntiforgery(o =>
        {
            o.HeaderName = "X-Wedding-CSRF";
            o.Cookie.HttpOnly = true;
            o.Cookie.IsEssential = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = options.DevelopmentHttp ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        });
        if (options.Enabled)
        {
            services.PostConfigure<FormOptions>(o => o.MemoryBufferThreshold = 64 * 1024);
            services.PostConfigure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, o =>
            {
                o.Cookie.HttpOnly = true;
                o.Cookie.IsEssential = true;
                o.Cookie.SecurePolicy = options.DevelopmentHttp ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            });
            services.Configure<ForwardedHeadersOptions>(o =>
            {
                o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                o.ForwardLimit = 1;
                if (!string.IsNullOrWhiteSpace(options.TrustedProxy))
                    o.KnownProxies.Add(IPAddress.Parse(options.TrustedProxy));
            });
        }
        return services;
    }

    public static IApplicationBuilder UseWeddingPortal(this IApplicationBuilder app)
    {
        var options = app.ApplicationServices.GetRequiredService<WeddingOptions>();
        if (!options.Enabled) return app;
        var env = app.ApplicationServices.GetRequiredService<IWebHostEnvironment>();
        if (options.DevelopmentHttp && !env.IsDevelopment())
            throw new InvalidOperationException("Wedding:DevelopmentHttp is permitted only in Development.");
        var root = Path.GetFullPath(options.StoragePath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var content = Path.GetFullPath(env.ContentRootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (root.StartsWith(content, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Wedding storage must be outside the application and all public/static directories.");
        app.UseForwardedHeaders();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path == "/")
            {
                context.Response.Redirect("/Wedding");
                return;
            }
            await next();
        });
        return app;
    }
}
