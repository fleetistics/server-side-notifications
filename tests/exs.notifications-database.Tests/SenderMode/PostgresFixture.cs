using exs.notifications_database;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace exs.notifications_database.Tests.SenderMode
{
	/// <summary>
	/// One real PostgreSQL for the sender-mode tests, shared by every test in a class, with the schema built
	/// from the model. The migrations that produce the real schema are exercised by MigrationTests; what is
	/// being checked here is different: that the relay's, puller's and hooks' queries translate to SQL and run
	/// on Npgsql, which behaves differently from the SQLite the unit tests use (uuid[] lookups, bulk deletes
	/// and updates through a complex property, strict UTC timestamps).
	/// </summary>
	public sealed class PostgresFixture : IAsyncLifetime
	{
		private readonly PostgreSqlContainer mPostgres = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
		private DbContextOptions<NotificationDatabaseContext> mOptions = null!;

		public async ValueTask InitializeAsync()
		{
			await mPostgres.StartAsync();
			mOptions = new DbContextOptionsBuilder<NotificationDatabaseContext>().UseNpgsql(mPostgres.GetConnectionString()).Options;
			await using var context = CreateContext();
			await context.Database.EnsureCreatedAsync();
		}

		public async ValueTask DisposeAsync() => await mPostgres.DisposeAsync();

		public NotificationDatabaseContext CreateContext() => new(mOptions);

		/// <summary>Empties every table, so each test starts from nothing and tests need not use distinct ids.</summary>
		public async Task ResetAsync()
		{
			await using var context = CreateContext();
			await context.Database.ExecuteSqlRawAsync(
				"TRUNCATE notification_outbox, notification_transport_cursor, fcm_queue, fcm_sent, notification2user, notification, user_session RESTART IDENTITY CASCADE");
		}

		public async Task<T> QueryAsync<T>(Func<NotificationDatabaseContext, Task<T>> query)
		{
			await using var context = CreateContext();
			return await query(context);
		}
	}
}
