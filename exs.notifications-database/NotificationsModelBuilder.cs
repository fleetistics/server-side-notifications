using exs.dbContextCommons;
using exs.notifications_model.Notifications;
using exs.notifications_model.Transports;
using Microsoft.EntityFrameworkCore;

namespace exs.notifications_database
{
	public static class NotificationsModelBuilder
	{
		public static void CreateModel(ModelBuilder modelBuilder)
		{
			if (_created) return;

			CommonModelBuilder.CreateModel(modelBuilder);

			modelBuilder.Entity<NotificationType>(entity =>
			{
				entity.ToTable("notification_type");
				entity.HasKey(e => new { e.Id });
				entity.Property(e => e.Id).ValueGeneratedOnAdd();
			});
			modelBuilder.Entity<Notification>(entity =>
			{
				entity.ToTable("notification");
				entity.HasKey(e => new { e.Id });
				entity.Property(e => e.Id).ValueGeneratedOnAdd();
				// Partial index for NotificationService's unprocessed-notification sweep
				// (Where(n => n.Status == New)). notification is an append-only log that grows forever,
				// so an unfiltered index on Status would grow with it while only ever being used to find
				// the handful of rows that are still New. Filtering the index to Status = 0 means it
				// holds exactly the backlog - rows leave it the moment the status flips - so the sweep
				// costs O(work outstanding) rather than O(history), which is what makes it affordable to
				// run periodically rather than only at startup.
				//
				// Date rather than Id as the indexed column: the sweep's other predicate is the
				// expiry check against Date, currently evaluated in memory but a candidate to push
				// into SQL, and ordering oldest-first is the natural drain order either way.
				//
				// The filter is raw SQL, so it uses the real column name. These tables have snake_case
				// names but PascalCase columns (no naming convention is configured anywhere), hence the
				// quoted "Status" - unquoted status would silently target a column that does not exist.
				entity.HasIndex(e => e.Date)
					.HasDatabaseName("IX_notification_unprocessed")
					.HasFilter("\"Status\" = 0");
			});
			modelBuilder.Entity<NotificationToUser>(entity =>
			{
				entity.HasKey(e => new { e.UserId, e.NotificationId });
				entity.ToTable("notification2user");
				entity.HasOne(e => e.Notification).WithMany().HasForeignKey(e => e.NotificationId);
			});
			modelBuilder.Entity<FcmQueue>(entity =>
			{
				entity.HasKey(e => new { e.Id });
				entity.Property(e => e.Id).ValueGeneratedOnAdd();
				entity.ToTable("fcm_queue");
				entity.HasOne(e => e.Notification).WithMany().HasForeignKey(e => e.NotificationId);
				// Worker.pollOnceAsync runs OrderBy(q => q.Date).Take(BatchSize) every poll interval -
				// without this it sorts the whole table each time. Unfiltered (unlike the notification
				// one) because fcm_queue is a true queue: rows are deleted once processed, so the table
				// is already only the outstanding work and the index stays small on its own.
				entity.HasIndex(e => e.Date).HasDatabaseName("IX_fcm_queue_Date");
			});
			modelBuilder.Entity<FcmSent>(entity =>
			{
				entity.HasKey(e => new { e.Id });
				entity.Property(e => e.Id).ValueGeneratedOnAdd();
				entity.ToTable("fcm_sent");
				// NotificationQueueProcessor's resume path looks up already-delivered sessions by
				// (NotificationId, UserId) before re-sending a partly-sent queue row. Unlike fcm_queue
				// this table is an append-only delivery log that is never pruned, so the lookup degrades
				// with total history rather than with outstanding work - the one place a missing index
				// would get measurably worse every week. The remaining predicates (UserSessionId,
				// FcmToken, Error) stay unindexed: they filter a handful of rows per notification once
				// these two columns have done the selective work.
				entity.HasIndex(e => new { e.NotificationId, e.UserId }).HasDatabaseName("IX_fcm_sent_NotificationId_UserId");
			});

			_created = true;
		}

		// NOTE ON TABLE OWNERSHIP - read this before running `dotnet ef migrations add` here.
		//
		// This context maps more tables than this repo owns. client_device_platform, user_session,
		// notification_type and notification2user are created and altered by
		// server-side-base-api-server's migrations (main-database/Migrations, against
		// MainDatabaseContext). Here they are read-only inputs, mapped only so the queries in
		// NotificationQueueProcessor and NotificationService can be written against them.
		//
		// The scaffolder cannot know that, so it emits CreateTable for them in any migration generated
		// from an empty history. The initial migration was hand-edited to strip those; do the same for
		// any future migration that touches them, and make the change in the api-server repo instead.
		//
		// ExcludeFromMigrations() looks like the tidy fix and was tried - it is not, because
		// EnsureCreated() honours it too, which empties those tables out of the SQLite schema the test
		// suite builds and breaks every test that seeds a UserSession.
		//
		// notification is the one shared table this repo does migrate: the table comes from the
		// api-server, but the Status column (the New/Processed/Expired state machine NotificationService
		// drives) exists only in this repo's model, so this repo has to own that column.

		private static bool _created = false;
	}
}
