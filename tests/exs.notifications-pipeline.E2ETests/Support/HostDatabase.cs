using System.Data.Common;
using exs.Database.Commons.Impl;
using exs.Database.Commons.Interfaces;
using exs.notifications_database;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace exs.notifications_pipeline.E2ETests.Support
{
	/// <summary>
	/// The host's database on a temporary SQLite file. A file, because the relay, the puller and the fan-out
	/// each open their own context at the same time; SQLite has no now(), which the user_session defaults
	/// refer to, so every connection gets one as it opens.
	/// </summary>
	public sealed class HostDatabase : IAsyncDisposable
	{
		private readonly string mPath = Path.Combine(Path.GetTempPath(), $"e2e-host-{Guid.NewGuid():N}.db");
		private readonly DbContextOptions<NotificationDatabaseContext> mOptions;

		private HostDatabase()
		{
			mOptions = new DbContextOptionsBuilder<NotificationDatabaseContext>()
				.UseSqlite($"Data Source={mPath};Default Timeout=30;Pooling=False")
				.AddInterceptors(new NowFunctionInterceptor())
				.Options;
		}

		public static HostDatabase Create()
		{
			var database = new HostDatabase();
			using var context = database.CreateContext();
			context.Database.EnsureCreated();
			context.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
			return database;
		}

		public NotificationDatabaseContext CreateContext() => new(mOptions);

		public void Register(IServiceCollection services)
		{
			services.AddScoped(_ => CreateContext());
			services.AddScoped<IRepository>(provider => new EntityFrameworkRepository<NotificationDatabaseContext>(provider.GetRequiredService<NotificationDatabaseContext>()));
			services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
		}

		public async Task<T> QueryAsync<T>(Func<NotificationDatabaseContext, Task<T>> query)
		{
			await using var context = CreateContext();
			return await query(context);
		}

		public async ValueTask DisposeAsync()
		{
			SqliteConnection.ClearAllPools();
			foreach (var file in new[] { mPath, mPath + "-wal", mPath + "-shm" })
			{
				try { File.Delete(file); } catch { /* temp file */ }
			}
			await Task.CompletedTask;
		}

		private sealed class NowFunctionInterceptor : DbConnectionInterceptor
		{
			public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
			{
				if (connection is SqliteConnection sqlite) sqlite.CreateFunction("now", () => DateTime.UtcNow);
			}

			public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
			{
				ConnectionOpened(connection, eventData);
				return Task.CompletedTask;
			}
		}
	}
}
