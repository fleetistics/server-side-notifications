using System.Data.Common;
using exs.Database.Commons.Impl;
using exs.Database.Commons.Interfaces;
using exs.notifications_database;
using exs.notifications_model.Notifications;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace exs.notifications_service.Tests.Sender.Support
{
	/// <summary>
	/// The host's notification database on a temporary SQLite file. A file rather than an in-memory
	/// connection because the relay and puller open several contexts at once, which one shared SQLite
	/// connection cannot serve. SQLite has no now(), which the user_session defaults refer to, so every
	/// connection gets one registered as it opens.
	/// </summary>
	public sealed class HostTestDatabase : IAsyncDisposable
	{
		private readonly string mPath = Path.Combine(Path.GetTempPath(), $"host-tests-{Guid.NewGuid():N}.db");
		private readonly DbContextOptions<NotificationDatabaseContext> mOptions;

		private HostTestDatabase()
		{
			mOptions = new DbContextOptionsBuilder<NotificationDatabaseContext>()
				.UseSqlite($"Data Source={mPath};Default Timeout=30;Pooling=False")
				.AddInterceptors(new NowFunctionInterceptor())
				.Options;
		}

		public static HostTestDatabase Create()
		{
			var database = new HostTestDatabase();
			using var context = database.CreateContext();
			context.Database.EnsureCreated();
			context.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
			return database;
		}

		public NotificationDatabaseContext CreateContext() => new(mOptions);

		public IRepository CreateRepository() => new EntityFrameworkRepository<NotificationDatabaseContext>(CreateContext());

		/// <summary>Registers what the relay, puller and fan-out need from the host: a scoped context and repository.</summary>
		public void Register(IServiceCollection services)
		{
			services.AddScoped(_ => CreateContext());
			services.AddScoped<IRepository>(provider => new EntityFrameworkRepository<NotificationDatabaseContext>(provider.GetRequiredService<NotificationDatabaseContext>()));
			services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
		}

		public async Task<T> QueryAsync<T>(Func<NotificationDatabaseContext, Task<T>> query)
		{
			await using var context = CreateContext();
			return await query(context);
		}

		public async Task<Notification> AddNotificationAsync(string? title = "Title", string? body = "Body", string? payload = null, short typeId = 1, int? entityId = null, byte transportType = NotificationTransportType.FCM)
		{
			await using var context = CreateContext();
			var notification = new Notification { Date = DateTime.UtcNow, TypeId = typeId, EntityId = entityId, Title = title, Body = body, Payload = payload, TransportType = transportType };
			context.Set<Notification>().Add(notification);
			await context.SaveChangesAsync();
			return notification;
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
