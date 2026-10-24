using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("exs.notifications-service.Tests")]
[assembly: InternalsVisibleTo("exs.notifications-pipeline.E2ETests")]
[assembly: InternalsVisibleTo("exs.notifications-database.Tests")] // runs the relay, puller and hooks against a real PostgreSQL
