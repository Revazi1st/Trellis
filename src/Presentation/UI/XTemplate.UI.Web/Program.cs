var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/", () => "XTemplate.UI.Web — the Blazor Web App arrives at M7.");

await app.RunAsync();
