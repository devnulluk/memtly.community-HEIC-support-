using Memtly.Core.Extensions;
using Memtly.Community.Wedding;

namespace Memtly.Community;

public class Startup
{
    public static bool Ready = false;
    public Startup(IConfiguration configuration) => Configuration = configuration;
    public IConfiguration Configuration { get; }

    public void ConfigureServices(IServiceCollection services)
    {
        services.ConfigureCommunityServices();
        services.AddWeddingPortal(Configuration);
        Ready = true;
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        app.UseWeddingPortal();
        app.ConfigureCommunity(env);
    }
}
