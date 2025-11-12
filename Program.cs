using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Options;
using Lararafelagid.Services;
using Lararafelagid.Services.MostViewed;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers(); // <-- no AddViewLocalization / AddDataAnnotationsLocalization
builder.Services.AddHttpClient();
builder.Services.AddSingleton<PlausibleService>();
builder.Services.AddMemoryCache();

builder.Services.AddSingleton<ViewCountService>();
builder.Services.AddSingleton<IViewCountService>(sp => sp.GetRequiredService<ViewCountService>());
builder.Services.AddSingleton<IMostViewedQueryService>(sp => sp.GetRequiredService<ViewCountService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<ViewCountService>());

// Keep localization services registered (no harm, no view/data probing)
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");

// ---- Global culture: fo-FO only ----
var fo = new CultureInfo("fo-FO");

// Request localization config (fixed to FO)
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.DefaultRequestCulture = new RequestCulture(fo);
    options.SupportedCultures    = new List<CultureInfo> { fo };
    options.SupportedUICultures  = new List<CultureInfo> { fo };

    // Always return fo-FO; note the nullable generic arg here:
    options.RequestCultureProviders = new List<IRequestCultureProvider>
    {
        new CustomRequestCultureProvider(_ =>
            Task.FromResult<ProviderCultureResult?>(new ProviderCultureResult(fo.Name, fo.Name)))
    };

    options.FallBackToParentCultures = true;
    options.FallBackToParentUICultures = true;
});

// Post-configure (idempotent)
builder.Services.PostConfigure<RequestLocalizationOptions>(options =>
{
    options.SupportedCultures   = (options.SupportedCultures   as List<CultureInfo>) ?? options.SupportedCultures?.ToList()   ?? new List<CultureInfo>();
    options.SupportedUICultures = (options.SupportedUICultures as List<CultureInfo>) ?? options.SupportedUICultures?.ToList() ?? new List<CultureInfo>();

    if (!options.SupportedCultures.Any(c => c.Name == fo.Name)) options.SupportedCultures.Add(fo);
    if (!options.SupportedUICultures.Any(c => c.Name == fo.Name)) options.SupportedUICultures.Add(fo);

    options.DefaultRequestCulture = new RequestCulture(fo);
    options.RequestCultureProviders = new List<IRequestCultureProvider>
    {
        new CustomRequestCultureProvider(_ =>
            Task.FromResult<ProviderCultureResult?>(new ProviderCultureResult(fo.Name, fo.Name)))
    };
});

// Background threads too
CultureInfo.DefaultThreadCurrentCulture   = fo;
CultureInfo.DefaultThreadCurrentUICulture = fo;

// ---- Umbraco setup ----
builder.CreateUmbracoBuilder()
    .AddBackOffice()
    .AddWebsite()
    .AddDeliveryApi()
    .AddComposers()
    .Build();

var app = builder.Build();

await app.BootUmbracoAsync();

// Apply localization before Umbraco middleware
app.UseRequestLocalization(app.Services.GetRequiredService<IOptions<RequestLocalizationOptions>>().Value);

app.UseUmbraco()
    .WithMiddleware(u =>
    {
        u.UseBackOffice();
        u.UseWebsite();
    })
    .WithEndpoints(u =>
    {
        u.UseInstallerEndpoints();
        u.UseBackOfficeEndpoints();
        u.UseWebsiteEndpoints();
    });

await app.RunAsync();
