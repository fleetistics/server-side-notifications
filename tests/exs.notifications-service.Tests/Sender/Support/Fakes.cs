using System.Collections.Concurrent;
using exs.Database.Commons.Interfaces;
using exs.notifications_contracts;
using exs.notifications_model.Notifications;
using exs.notifications_service.Sender;
using Microsoft.Extensions.Logging;

namespace exs.notifications_service.Tests.Sender.Support
{
	/// <summary>A clock that only moves when a test says so.</summary>
	public sealed class ManualTimeProvider : TimeProvider
	{
		private DateTimeOffset mNow = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

		public override DateTimeOffset GetUtcNow() => mNow;

		public void Advance(TimeSpan by) => mNow += by;
	}

	public sealed class FakeNotificationTransport : INotificationTransport
	{
		private readonly ConcurrentQueue<string> mSubmitted = new();
		private readonly ConcurrentQueue<long> mFetchPositions = new();
		private int mSubmitCount;

		public IReadOnlyList<string> Submitted => mSubmitted.ToArray();

		public IReadOnlyList<long> FetchPositions => mFetchPositions.ToArray();

		/// <summary>Receives the envelope JSON and the 1-based submission number.</summary>
		public Func<string, int, SubmitResult> OnSubmit { get; set; } = (_, _) => new SubmitResult(SubmitOutcome.Accepted);

		public Func<long, int, ResultPage> OnFetch { get; set; } = (after, _) => new ResultPage { Results = [], NextAfter = after };

		public Task<SubmitResult> SubmitAsync(string envelopeJson, CancellationToken cancellationToken)
		{
			mSubmitted.Enqueue(envelopeJson);
			return Task.FromResult(OnSubmit(envelopeJson, Interlocked.Increment(ref mSubmitCount)));
		}

		public Task<ResultPage> FetchResultsAsync(long after, int limit, CancellationToken cancellationToken)
		{
			mFetchPositions.Enqueue(after);
			return Task.FromResult(OnFetch(after, limit));
		}
	}

	public sealed class FakeFcmDestinationResolver : IFcmDestinationResolver
	{
		public List<FcmResolvedDestination> Destinations { get; } = new();

		public int Calls { get; private set; }

		public Exception? Throws { get; set; }

		public Task<IReadOnlyList<FcmResolvedDestination>> ResolveAsync(IRepository repository, Notification notification, IReadOnlyCollection<int> userIds, CancellationToken cancellationToken)
		{
			Calls++;
			if (Throws != null) throw Throws;
			return Task.FromResult<IReadOnlyList<FcmResolvedDestination>>(Destinations.Where(d => userIds.Contains(d.UserId)).ToList());
		}
	}

	public sealed class FakeFcmTokenInvalidator : IFcmTokenInvalidator
	{
		private readonly ConcurrentQueue<(int UserId, string? Ref, string Destination)> mCalls = new();

		public IReadOnlyList<(int UserId, string? Ref, string Destination)> Calls => mCalls.ToArray();

		public Exception? Throws { get; set; }

		public Task<bool> InvalidateAsync(IRepository repository, int userId, string? @ref, string destination, CancellationToken cancellationToken)
		{
			mCalls.Enqueue((userId, @ref, destination));
			if (Throws != null) throw Throws;
			return Task.FromResult(true);
		}
	}

	public sealed class FakeEmailDestinationResolver : IEmailDestinationResolver
	{
		public List<EmailResolvedDestination> Destinations { get; } = new();

		public int Calls { get; private set; }

		public Exception? Throws { get; set; }

		public Task<IReadOnlyList<EmailResolvedDestination>> ResolveAsync(IRepository repository, Notification notification, IReadOnlyCollection<int> userIds, CancellationToken cancellationToken)
		{
			Calls++;
			if (Throws != null) throw Throws;
			return Task.FromResult<IReadOnlyList<EmailResolvedDestination>>(Destinations.Where(d => userIds.Contains(d.UserId)).ToList());
		}
	}

	public sealed class FakeEmailAddressInvalidator : IEmailAddressInvalidator
	{
		private readonly ConcurrentQueue<(int UserId, string? Ref, string Address)> mCalls = new();

		public IReadOnlyList<(int UserId, string? Ref, string Address)> Calls => mCalls.ToArray();

		public Exception? Throws { get; set; }

		public Task<bool> InvalidateAsync(IRepository repository, int userId, string? @ref, string address, CancellationToken cancellationToken)
		{
			mCalls.Enqueue((userId, @ref, address));
			if (Throws != null) throw Throws;
			return Task.FromResult(true);
		}
	}

	public sealed class FakeEmailContentFactory : IEmailContentFactory
	{
		public Func<Notification, EmailResolvedDestination, EmailContent?> OnBuild { get; set; } = (n, _) => new EmailContent(n.Title ?? "", n.Body);

		public EmailContent? Build(Notification notification, EmailResolvedDestination destination) => OnBuild(notification, destination);
	}

	public sealed class ListLogger<T> : ILogger<T>
	{
		public List<(LogLevel Level, string Message)> Entries { get; } = new();

		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
			Entries.Add((logLevel, formatter(state, exception)));
	}
}
