using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Migrations;
using exs.dbContextCommons;

namespace exs.notifications_database
{
	/// <summary>
	/// Used only by `dotnet ef` at design time (migrations add/script/update). The worker host can't
	/// serve as the EF startup project: its config loading is relative to the built output folder and
	/// reads /local_configs, which the tooling has no way to boot.
	///
	/// The two ReplaceService calls mirror DatabaseDiHelper.AddDbServices exactly - they are what make
	/// migrations generated here behave like the ones in server-side-base-api-server (no DROP TABLE /
	/// DROP COLUMN ever scripted, LatestUpdate trigger DDL emitted). Leaving them out would silently
	/// produce migrations with different semantics than the rest of the estate.
	/// </summary>
	public sealed class NotificationDatabaseContextFactory : IDesignTimeDbContextFactory<NotificationDatabaseContext>
	{
		public NotificationDatabaseContext CreateDbContext(string[] args)
		{
			// `migrations add` never opens a connection - it only needs a parseable string to build the
			// Npgsql provider - so the placeholder is enough to author migrations with no secrets and no
			// database reachable. `database update`/`script --idempotent` against a real database require
			// ConnectionStrings__MainDatabase in the environment; deliberately not defaulted to a live
			// host, so an absent-minded `database update` can't touch shared infrastructure.
			var connectionString =
				Environment.GetEnvironmentVariable("ConnectionStrings__MainDatabase")
				?? "Host=localhost;Database=notifications-design-time;Username=postgres";

			// No UseNetTopologySuite() here, unlike AddDbServices: nothing this context maps has a
			// spatial column, and enabling it puts a CREATE EXTENSION postgis into every migration
			// generated from this repo - DDL that belongs to the api-server's migrations and that the
			// migration role may not even have rights to execute.
			var options = new DbContextOptionsBuilder<NotificationDatabaseContext>()
				.UseNpgsql(connectionString, opts => opts.MigrationsHistoryTable(MIGRATIONS_HISTORY_TABLE))
				.ReplaceService<IMigrationsSqlGenerator, LatestUpdateTriggerSqlGenerator>()
				.ReplaceService<IMigrationsModelDiffer, NoDropMigrationsModelDiffer>()
				.Options;

			return new NotificationDatabaseContext(options);
		}

		/// <summary>
		/// NotificationDatabaseContext and the api-server's MainDatabaseContext are two contexts over one
		/// physical database - this pipeline reads user_session and notification, which that repo's
		/// migrations create. Both would default to "__EFMigrationsHistory", interleaving two repos'
		/// migration records in one table with nothing but the timestamp prefix to tell them apart, and
		/// leaving neither repo's `migrations list` able to describe the database honestly.
		///
		/// Separate history tables keep the bookkeeping independent. They do NOT make the schemas
		/// independent: there is still one notification table, this repo's first migration ALTERs it and
		/// FKs to it, so the api-server's migrations must have run before these are applied to a fresh
		/// database. That ordering is a real dependency no tooling enforces.
		///
		/// If anything ever calls Database.Migrate() at runtime (nothing does today - the worker's
		/// Program.cs only runs the host), it must pass this same table name. Using the default there
		/// would present EF with an empty history, so it would try to re-apply InitialFcmTransport and
		/// fail on "column Status already exists".
		/// </summary>
		public const string MIGRATIONS_HISTORY_TABLE = "__EFMigrationsHistory_Notifications";
	}
}
