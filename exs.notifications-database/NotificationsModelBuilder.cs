using exs.dbContextCommons;
using exs.notifications_model.Notifications;
using exs.notifications_model.Outbox;
using exs.notifications_model.Transports;
using Microsoft.EntityFrameworkCore;

namespace exs.notifications_database
{
	public static class NotificationsModelBuilder
	{
		/// <summary>
		/// For a host that keeps user_session in the estate's shared shape: the shared user/session/platform
		/// entities plus the notification tables.
		/// </summary>
		public static void CreateModel(ModelBuilder modelBuilder)
		{
			CommonModelBuilder.CreateModel(modelBuilder);
			CreateNotificationModel(modelBuilder);
		}

		/// <summary>
		/// Only the notification tables (notification, notification_type, notification2user, fcm_queue,
		/// fcm_sent, notification_outbox, notification_transport_cursor), for a host that maps user_session
		/// itself - as pssp.api does, with its own flatter entity. Mapping the shared UserSession as well
		/// would put two entity types on one table.
		/// </summary>
		public static void CreateNotificationModel(ModelBuilder modelBuilder)
		{
			// Per model, not per process: EF builds a separate model for every distinct set of context options
			// (a test, or a host that configures its context in two places, can have two), and each needs
			// the tables mapped. A process-wide flag left every model after the first without them.
			if (modelBuilder.Model.FindEntityType(typeof(Notification)) != null) return;

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
				// Push unless a writer says otherwise: the database default is what makes rows that predate the column,
				// and writers that never set it, mean push. (Unlike Status, whose default was dropped after its backfill,
				// because there "unset" must not mean "already Processed".)
				entity.Property(e => e.TransportType).HasDefaultValue(NotificationTransportType.FCM);
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
			modelBuilder.Entity<OutboxMessage>(entity =>
			{
				entity.ToTable("notification_outbox");
				entity.HasKey(e => e.Id);
				entity.Property(e => e.Id).ValueGeneratedOnAdd();
				entity.Property(e => e.Channel).HasMaxLength(32).IsRequired();
				entity.Property(e => e.Account).HasMaxLength(64);
				entity.Property(e => e.Ref).HasMaxLength(256);
				entity.Property(e => e.SubmitError).HasMaxLength(1000);
				entity.Property(e => e.ResultStatus).HasMaxLength(32);
				entity.Property(e => e.ErrorKind).HasMaxLength(32);
				entity.Property(e => e.ErrorCode).HasMaxLength(128);
				entity.Property(e => e.ErrorMessage).HasMaxLength(1000);
				entity.Property(e => e.ProviderMessageId).HasMaxLength(256);
				// Optional: a free-form email (INotificationSender.CreateEmail) has no notification, and no user either.
				entity.HasOne(e => e.Notification).WithMany().HasForeignKey(e => e.NotificationId).IsRequired(false).OnDelete(DeleteBehavior.Cascade);

				// The sender deduplicates on this, and the result puller finds a row by it: both need it unique.
				entity.HasIndex(e => e.MessageId).IsUnique().HasDatabaseName("UX_notification_outbox_MessageId");

				// The relay only ever looks for Pending rows, oldest first. Filtered to Pending it stays the size
				// of the backlog rather than of the retained history (the "State" column is PascalCase, so quoted).
				entity.HasIndex(e => e.CreatedAt).HasDatabaseName("IX_notification_outbox_pending").HasFilter("\"State\" = 0");

				// "Did this user's destinations get it" and the fan-out's own already-done check.
				entity.HasIndex(e => new { e.NotificationId, e.UserId }).HasDatabaseName("IX_notification_outbox_NotificationId_UserId");

				entity.HasIndex(e => e.CompletedAt).HasDatabaseName("IX_notification_outbox_CompletedAt");
			});
			modelBuilder.Entity<TransportCursor>(entity =>
			{
				entity.ToTable("notification_transport_cursor");
				entity.HasKey(e => e.Name);
				entity.Property(e => e.Name).HasMaxLength(64);
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
	}
}
