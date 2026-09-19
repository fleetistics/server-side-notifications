using exs.Database.Commons.Impl;
using exs.Database.Commons.Interfaces;
using exs.notifications_database;
using exs.notifications_model.Notifications;
using exs.notifications_service.Impl;
using exs.notifications_service.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace exs.notifications_service.Tests
{
	/// <summary>
	/// Backed by a real NotificationDatabaseContext so notifications get real, distinct auto-generated
	/// Ids from SaveAsync - NotificationSender.Dispose() matches its buffered NotificationToUser rows
	/// back to their Notification by Id, which is meaningless against a fake repository that never
	/// assigns one.
	/// </summary>
	public sealed class NotificationSenderTests : IAsyncLifetime
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
		public async Task CreateNotification_EmptyUserIds_Throws()
		{
			var sender = new NotificationSender(new FakeNotificationService(), NullLogger<NotificationSender>.Instance);
			await using var context = createContext();
			IRepository repository = new EntityFrameworkRepository<NotificationDatabaseContext>(context);

			Should.Throw<ArgumentException>(() =>
				sender.CreateNotification(repository, notificationType: 1, entityId: 0, "Title", "Body", "", Array.Empty<int>()));
		}

		[Fact]
		public void Dispose_NoNotificationsCreated_EnqueuesNothing()
		{
			var fakeService = new FakeNotificationService();
			var sender = new NotificationSender(fakeService, NullLogger<NotificationSender>.Instance);

			((IDisposable)sender).Dispose();

			fakeService.EnqueuedCalls.ShouldBeEmpty();
		}

		[Fact]
		public async Task CreateNotification_ThenDispose_EnqueuesItWithItsUsers()
		{
			var fakeService = new FakeNotificationService();
			var sender = new NotificationSender(fakeService, NullLogger<NotificationSender>.Instance);
			await using var context = createContext();
			IRepository repository = new EntityFrameworkRepository<NotificationDatabaseContext>(context);

			sender.CreateNotification(repository, notificationType: 1, entityId: 42, "Title", "Body", "{}", [10, 20]);
			await repository.SaveAsync(); // a real caller always saves before its DI scope (and this sender) disposes

			((IDisposable)sender).Dispose();

			fakeService.EnqueuedCalls.Count.ShouldBe(1);
			var call = fakeService.EnqueuedCalls[0];
			call.Notification.Title.ShouldBe("Title");
			call.Notification.EntityId.ShouldBe(42);
			call.UserIds.ShouldBe([10, 20], ignoreOrder: true);
		}

		[Fact]
		public async Task MultipleNotificationsCreatedBeforeDispose_EachEnqueuedWithOnlyItsOwnUsers()
		{
			// Regression guard: Dispose() matches buffered NotificationToUser rows back to their
			// Notification by Id. If SaveAsync weren't called between creates (or ever), every
			// Notification here would still have Id == 0 and this match would silently pool every
			// user from every notification created in the same scope into each one.
			var fakeService = new FakeNotificationService();
			var sender = new NotificationSender(fakeService, NullLogger<NotificationSender>.Instance);
			await using var context = createContext();
			IRepository repository = new EntityFrameworkRepository<NotificationDatabaseContext>(context);

			sender.CreateNotification(repository, notificationType: 1, entityId: 0, "First", "Body", "", [1]);
			sender.CreateNotification(repository, notificationType: 1, entityId: 0, "Second", "Body", "", [2, 3]);
			await repository.SaveAsync();

			((IDisposable)sender).Dispose();

			fakeService.EnqueuedCalls.Count.ShouldBe(2);
			fakeService.EnqueuedCalls.Single(c => c.Notification.Title == "First").UserIds.ShouldBe([1]);
			fakeService.EnqueuedCalls.Single(c => c.Notification.Title == "Second").UserIds.ShouldBe([2, 3], ignoreOrder: true);
		}

		[Fact]
		public async Task Dispose_StopsEnqueuingAfterTheFirstQueueFullFailure()
		{
			var fakeService = new FakeNotificationService { NextResults = new Queue<bool>([false, true]) };
			var sender = new NotificationSender(fakeService, NullLogger<NotificationSender>.Instance);
			await using var context = createContext();
			IRepository repository = new EntityFrameworkRepository<NotificationDatabaseContext>(context);

			sender.CreateNotification(repository, notificationType: 1, entityId: 0, "First", "Body", "", [1]);
			sender.CreateNotification(repository, notificationType: 1, entityId: 0, "Second", "Body", "", [2]);
			await repository.SaveAsync();

			((IDisposable)sender).Dispose();

			fakeService.EnqueuedCalls.Count.ShouldBe(1); // stopped after the first (failed) attempt
		}

		private NotificationDatabaseContext createContext() =>
			new(new DbContextOptionsBuilder<NotificationDatabaseContext>().UseSqlite(mConnection).Options);

		private sealed class FakeNotificationService : INotificationService
		{
			public List<(Notification Notification, List<int> UserIds)> EnqueuedCalls { get; } = [];
			public Queue<bool>? NextResults { get; set; }

			public bool EnqueueNotification(Notification notification, List<int> userIds)
			{
				EnqueuedCalls.Add((notification, userIds));
				return NextResults is { Count: > 0 } ? NextResults.Dequeue() : true;
			}
		}
	}
}
