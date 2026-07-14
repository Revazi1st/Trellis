using Scalar.AspNetCore;
using XTemplate.Api.Common;
using XTemplate.Api.Common.Endpoints;
using XTemplate.Application;
using XTemplate.Infrastructure;
using XTemplate.Persistence;

var builder = WebApplication.CreateBuilder(args);

// M5 replaces manual configuration with Aspire ServiceDefaults + client integrations.
builder.Services.AddApplication();
builder.Services.AddInfrastructure();
builder.Services.AddPersistence(builder.Configuration.GetConnectionString("xtemplate-db")
    ?? throw new InvalidOperationException("Connection string 'xtemplate-db' is not configured."));
builder.Services.AddApiCommon();
builder.Services.AddApiDocumentation();

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapGroup("/api/v1").MapEndpoints(typeof(Program).Assembly);

await app.RunAsync();

/// <summary>Marker for endpoint discovery and WebApplicationFactory-style test hosts.</summary>
public partial class Program
{
    protected Program()
    {
    }
}
