var builder = Host.CreateApplicationBuilder(args);

// Migration + seeding worker (apply EF migrations, seed roles/permissions/admin/sample data, then exit) is implemented at M5.

await builder.Build().RunAsync();
