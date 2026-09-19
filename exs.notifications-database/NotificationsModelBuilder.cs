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
			});
			modelBuilder.Entity<FcmSent>(entity =>
			{
				entity.HasKey(e => new { e.Id });
				entity.Property(e => e.Id).ValueGeneratedOnAdd();
				entity.ToTable("fcm_sent");
			});
			_created = true;
		}

		private static bool _created = false;
	}
}
