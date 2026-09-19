using exs.dbContextCommons;
using Microsoft.EntityFrameworkCore;

namespace exs.notifications_database
{
	public class NotificationDatabaseContext : BaseDatabaseContext
	{
		public NotificationDatabaseContext(DbContextOptions options) : base(options) { }

		protected override void createModel(ModelBuilder modelBuilder)
		{
			NotificationsModelBuilder.CreateModel(modelBuilder);
		}
	}
}
