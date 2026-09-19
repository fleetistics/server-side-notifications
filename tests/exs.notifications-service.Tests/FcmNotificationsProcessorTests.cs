using exs.Database.Commons.Impl;
using exs.Database.Commons.Interfaces;
using exs.notifications_database;
using exs.notifications_model.Notifications;
using exs.notifications_model.Transports;
using exs.notifications_service.Impl;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace exs.notifications_service.Tests
{
	public sealed class FcmNotificationsProcessorTests : IAsyncLifetime
	{
		private readonly SqliteConnection mConnection = new("Filename=:memory:");

		public async ValueTask InitializeAsync()
		{
			await mConnection.OpenAsync();
			mConnection.CreateFunction("now", () => DateTime.UtcNow);
			await using var context = createContext();
			await context.Database.EnsureCreatedAsync();
		}

		public async ValueTask DisposeAsync() => await mConnection.DisposeAsync();

		[Fact]
		public void TransportType_IsFcm()
		{
			new FcmNotificationsProcessor().TransportType.ShouldBe(NotificationTransportType.FCM);
		}

		[Fact]
		public async Task ProcessNotificationAsync_CreatesOneFcmQueueRowPerUserAndMarksNotificationProcessed()
		{
			await using var context = createContext();
			IRepository repository = new EntityFrameworkRepository<NotificationDatabaseContext>(context);
			var notification = new Notification { Date = DateTime.UtcNow, TypeId = 1, Title = "t", Body = "b", Payload = "" };
			repository.Create(notification);
			await repository.SaveAsync();

			await new FcmNotificationsProcessor().ProcessNotificationAsync(repository, notification, [1, 2, 3], CancellationToken.None);
			await repository.SaveAsync();

			notification.Status.ShouldBe(NotificationStatus.Processed);
			var queueRows = await context.Set<FcmQueue>().ToListAsync();
			queueRows.Count.ShouldBe(3);
			queueRows.Select(q => q.UserId).ShouldBe([1, 2, 3], ignoreOrder: true);
			queueRows.ShouldAllBe(q => q.NotificationId == notification.Id);
		}

		[Fact]
		public async Task ProcessNotificationAsync_NoUsers_CreatesNoQueueRowsButStillMarksProcessed()
		{
			await using var context = createContext();
			IRepository repository = new EntityFrameworkRepository<NotificationDatabaseContext>(context);
			var notification = new Notification { Date = DateTime.UtcNow, TypeId = 1, Title = "t", Body = "b", Payload = "" };
			repository.Create(notification);
			await repository.SaveAsync();

			await new FcmNotificationsProcessor().ProcessNotificationAsync(repository, notification, [], CancellationToken.None);
			await repository.SaveAsync();

			notification.Status.ShouldBe(NotificationStatus.Processed);
			(await context.Set<FcmQueue>().AnyAsync()).ShouldBeFalse();
		}

		private NotificationDatabaseContext createContext() =>
			new(new DbContextOptionsBuilder<NotificationDatabaseContext>().UseSqlite(mConnection).Options);
	}
}
