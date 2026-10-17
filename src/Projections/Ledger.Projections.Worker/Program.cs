var builder = Host.CreateApplicationBuilder(args);

using var host = builder.Build();

host.Run();
