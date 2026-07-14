var builder = DistributedApplication.CreateBuilder(args);

// Resource graph (postgres, migrations, api, web, compose environment) is wired at M5.

await builder.Build().RunAsync();
