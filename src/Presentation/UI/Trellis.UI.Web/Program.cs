#if (UseSerilog)
using Serilog;
#endif
using Trellis.ServiceDefaults;
using Trellis.UI.Common;
using Trellis.UI.Common.Services;
using Trellis.UI.Web;
using Trellis.UI.Web.Components;

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

// Only the host knows its environment; the shared UI reads the answer (e.g. the seeded-admin hint on /login).
bool isDevelopment = builder.Environment.IsDevelopment();
builder.Services.Configure<UiOptions>(options => options.ShowDevelopmentHints = isDevelopment);

var app = builder.Build();

// Security response headers, the browser-facing counterpart of the API's set (ADR-0021). Set when the
// response starts, so every response carries them: pages, static assets, the Blazor endpoints, and the
// error page (the exception handler clears headers before re-executing; OnStarting runs after that).
// - frame-ancestors 'none' (+ X-Frame-Options for old browsers): no page can be framed (clickjacking).
// - script-src 'self': only the app's own script files run, never an injected inline script. blazor.web.js
//   and the dev-time browser-refresh script are same-origin files, so nothing legitimate is blocked.
// - object-src 'none', base-uri 'self', form-action 'self': no plugins, no <base> hijack, no form posting
//   off-site. style-src stays open on purpose: Blazor's reconnect overlay injects an inline <style>.
// - Referrer-Policy same-origin: page URLs (which carry ids) never reach another origin. Unlike the API's
//   no-referrer, it keeps the real Origin on the app's own POSTs (no-referrer would send "Origin: null").
const string contentSecurityPolicy =
    "script-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
app.Use(static (context, next) =>
{
    context.Response.OnStarting(static state =>
    {
        IHeaderDictionary headers = ((HttpContext)state).Response.Headers;
        headers.ContentSecurityPolicy = contentSecurityPolicy;
        headers.XFrameOptions = "DENY";
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "same-origin";
        return Task.CompletedTask;
    }, context);

    return next(context);
});

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
    .AddAdditionalAssemblies(typeof(Trellis.UI.Common.DependencyInjection).Assembly);

await app.RunAsync();
