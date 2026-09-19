using exs.fcm_sender.core;
using Serilog;
using System.Reflection;

var builder = Host.CreateApplicationBuilder(args);

var directoryName = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location);

// Same config-layering convention as api-server: /configs is deploy-safe and always loaded,
// /local_configs (dev) or /../local_configs (prod, alongside the publish output rather than in
// it) carries the connection string, Firebase credentials path, etc.
if (Directory.Exists(directoryName + "/configs"))
{
	foreach (var jsonFilename in Directory.EnumerateFiles(directoryName + "/configs", "*.json", SearchOption.AllDirectories))
	{
		Console.WriteLine($"Loading /configs configuration: {jsonFilename}");
		builder.Configuration.AddJsonFile(jsonFilename, optional: false, reloadOnChange: false);
	}
}
if (builder.Environment.IsDevelopment())
{
	if (Directory.Exists(directoryName + "/local_configs"))
	{
		foreach (var jsonFilename in Directory.EnumerateFiles(directoryName + "/local_configs", "*.json", SearchOption.AllDirectories))
		{
			Console.WriteLine($"Loading dev /local_configs configuration: {jsonFilename}");
			builder.Configuration.AddJsonFile(jsonFilename, optional: false, reloadOnChange: false);
		}
	}
}
else
{
	if (Directory.Exists(directoryName + "/../local_configs"))
	{
		foreach (var jsonFilename in Directory.EnumerateFiles(directoryName + "/../local_configs", "*.json", SearchOption.AllDirectories))
		{
			Console.WriteLine($"Loading prod /local_configs configuration: {jsonFilename}");
			builder.Configuration.AddJsonFile(jsonFilename, optional: false, reloadOnChange: false);
		}
	}
}

builder.Services.AddSerilog((services, configuration) =>
{
	configuration
		.ReadFrom.Configuration(builder.Configuration)
		.ReadFrom.Services(services)
		.Enrich.FromLogContext()
		.Enrich.WithEnvironmentUserName()
		.Enrich.WithMachineName()
		.Enrich.WithProperty("Application", "exs.fcm-sender");
});

builder.Services.AddSystemd();

DiHelper.RegisterServices(builder.Services, builder.Configuration);

var host = builder.Build();
host.Run();
