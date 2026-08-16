#if (UseSerilog)
using Serilog;
#endif
using XTemplate.ServiceDefaults;
using XTemplate.UI.Common;
using XTemplate.UI.Common.Services;
using XTemplate.UI.Web;
using XTemplate.UI.Web.Components;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

#if (UseSerilog)
builder.Services.AddSerilog(static (services, config) => config
    .ReadFrom.Configuration(services.GetRequiredService<IConfiguration>())
    .Enrich.FromLogContext()
    // Explicit format provider: log output must not vary with the host's locale (CA1305).
    .WriteTo.Console(formatProvider: System.Globalization.CultureInfo.InvariantCulture));
#endif

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Per-circuit token store (Blazor Server) + shared UI services pointing at the API via service discovery.
builder.Services.AddScoped<ITokenStore, CircuitTokenStore>();
builder.Services.AddSharedUi(static (_, client) =>
    // Not a hardcoded URL: "https+http://api" is an Aspire service-discovery reference resolved to the API's real endpoint at runtime.
#pragma warning disable S1075
    client.BaseAddress = new Uri("https+http://api"));
#pragma warning restore S1075

var app = builder.Build();

app.MapDefaultEndpoints();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();
app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(XTemplate.UI.Common.DependencyInjection).Assembly);

await app.RunAsync();
