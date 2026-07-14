using XTemplate.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

var app = builder.Build();

app.MapDefaultEndpoints();

app.MapGet("/", () => "XTemplate.UI.Web — the Blazor Web App arrives at M7.");

await app.RunAsync();
