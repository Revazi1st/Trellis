var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/", () => "XTemplate.Api — feature endpoints arrive at M4.");

await app.RunAsync();
