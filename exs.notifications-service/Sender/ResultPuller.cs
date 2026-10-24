using exs.Database.Commons.Interfaces;
using exs.notifications_contracts;
using exs.notifications_model.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace exs.notifications_service.Sender
{
	/// <summary>
	/// Reads the sender's result log from a persisted cursor and applies each result to the outbox row it is
	/// about: that row becomes the delivery record for the user and destination, and a destination the sender
	/// reports dead for good is handed to the host's token invalidator.
	///
	/// Pulling (rather than the sender calling the host back) means the sender never needs to know where its
	/// hosts are, and a host that was down simply catches up from its cursor. Every step is idempotent, so two
	/// host instances running a puller, or a page applied twice after a crash, do no harm.
	/// </summary>
	public sealed class ResultPuller : BackgroundService
	{
		/// <summary>The row of notification_transport_cursor the FCM puller owns. Every channel has its own, named after it; see CursorNameFor.</summary>
		public const string CursorName = "fcm-sender";

		public static string CursorNameFor(string channel) => $"{channel}-sender";

		private readonly IServiceScopeFactory mScopeFactory;
		private readonly INotificationTransport mTransport;
		private readonly IServiceProvider mServices;
		private readonly SenderTransportOptions mOptions;
		private readonly TimeProvider mClock;
		private readonly ILogger<ResultPuller> mLogger;
		private readonly string mChannel;
		private readonly string mCursorName;

		// The invalidator is looked up when first needed rather than injected; see FcmSenderHooks.
		public ResultPuller(IServiceScopeFactory scopeFactory, INotificationTransport transport, IServiceProvider services, IOptions<SenderTransportOptions> options, TimeProvider clock, ILogger<ResultPuller> logger, string channel = Channels.Fcm)
		{
			mChannel = channel;
			mCursorName = CursorNameFor(channel);
			mScopeFactory = scopeFactory;
			mTransport = transport;
			mServices = services;
			mOptions = options.Value;
			mClock = clock;
			mLogger = logger;
		}

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			await Task.Yield();

			var failures = 0;
			while (!stoppingToken.IsCancellationRequested)
			{
				TimeSpan wait;
				try
				{
					var moreWaiting = await PullOnceAsync(stoppingToken);
					failures = 0;
					// A full page means there is more behind it: carry straight on rather than sleeping.
					wait = moreWaiting ? TimeSpan.Zero : TimeSpan.FromSeconds(mOptions.ResultPollIntervalSeconds);
				}
				catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
				{
					return;
				}
				catch (Exception ex)
				{
					failures++;
					// The sender being unreachable is routine enough that it should not spin or flood the log.
					var backoff = TimeSpan.FromSeconds(Math.Min(mOptions.RelayBackoffMaxSeconds, mOptions.ResultPollIntervalSeconds * Math.Pow(2, Math.Min(failures - 1, 10))));
					mLogger.LogWarning(ex, "Could not read the sender's results (failure {Failures}); trying again in {Seconds:0}s.", failures, backoff.TotalSeconds);
					wait = backoff;
				}

				try
				{
					if (wait > TimeSpan.Zero) await Task.Delay(wait, stoppingToken);
				}
				catch (OperationCanceledException)
				{
					return;
				}
			}
		}

		/// <summary>Reads and applies one page. Returns true when the page was full, so more is probably waiting.</summary>
		internal async Task<bool> PullOnceAsync(CancellationToken cancellationToken)
		{
			using var scope = mScopeFactory.CreateScope();
			var repository = scope.ServiceProvider.GetRequiredService<IRepository>();

			var cursor = await repository.GetQueryable<TransportCursor>(c => c.Name == mCursorName).SingleOrDefaultAsync(cancellationToken);
			var position = cursor?.Position ?? 0;

			var page = await mTransport.FetchResultsAsync(position, mOptions.ResultPageSize, cancellationToken);
			if (page.Results.Count == 0) return false;

			var messageIds = page.Results.Select(r => r.MessageId).ToList();
			var rows = (await repository.GetQueryable<OutboxMessage>(o => messageIds.Contains(o.MessageId)).ToListAsync(cancellationToken))
				.ToDictionary(r => r.MessageId);

			var toInvalidate = new List<(OutboxMessage Row, string Destination)>();
			foreach (var result in page.Results)
			{
				if (!rows.TryGetValue(result.MessageId, out var row))
				{
					// Pruned already, or not this host's. Nothing to record.
					mLogger.LogDebug("Result {ResultId} is for message {MessageId}, which has no outbox row; skipping it.", result.ResultId, result.MessageId);
					continue;
				}
				if (row.State == OutboxState.Completed) continue; // a page applied twice

				apply(row, result);
				if (result.DestinationInvalid && !string.IsNullOrEmpty(result.Destination))
				{
					toInvalidate.Add((row, result.Destination));
				}
			}

			if (cursor == null)
			{
				cursor = new TransportCursor { Name = mCursorName };
				repository.Create(cursor);
			}
			cursor.Position = Math.Max(cursor.Position, page.NextAfter);
			cursor.UpdatedAt = mClock.GetUtcNow().UtcDateTime;

			// The delivery record and the cursor are committed first and together. What follows is housekeeping:
			// if it fails the record survives, and the next notification to that dead token will simply be
			// reported dead again.
			await repository.SaveAsync(cancellationToken);

			foreach (var (row, destination) in toInvalidate)
			{
				try
				{
					await invalidateAsync(repository, row, destination, cancellationToken);
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					mLogger.LogWarning(ex, "Could not act on a dead {Channel} destination for user #{UserId} (message {MessageId}).", mChannel, row.UserId, row.MessageId);
				}
			}

			return page.Results.Count >= mOptions.ResultPageSize;
		}

		/// <summary>
		/// What "this destination is dead for good" means is the host's to say, and differs by channel. A push token
		/// has to be cleared or every later push to it is wasted, so the FCM hook is required. A bounced email address
		/// is only worth acting on if the host has a place to record it, so that hook is optional.
		/// </summary>
		private async Task invalidateAsync(IRepository repository, OutboxMessage row, string destination, CancellationToken cancellationToken)
		{
			// The hooks are keyed by user. A row without one is a free-form email (INotificationSender.CreateEmail): its
			// address was given by the caller, who has no user for the host to act on, so the bounce stays on the row.
			if (row.UserId is not { } userId)
			{
				// The address is not logged: it is personal data.
				mLogger.LogInformation("A {Channel} message with no user was refused as undeliverable (message {MessageId}); there is no user to hand to the invalidator, so it is recorded on the outbox row only.", mChannel, row.MessageId);
				return;
			}

			if (mChannel == Channels.Email)
			{
				var invalidator = mServices.GetService<IEmailAddressInvalidator>();
				if (invalidator == null)
				{
					// The address is not logged: it is personal data.
					mLogger.LogInformation("An email to user #{UserId} was refused as undeliverable (message {MessageId}); no IEmailAddressInvalidator is registered, so it is recorded on the outbox row only.", userId, row.MessageId);
					return;
				}
				await invalidator.InvalidateAsync(repository, userId, row.Ref, destination, cancellationToken);
				return;
			}

			await FcmSenderHooks.Require<IFcmTokenInvalidator>(mServices).InvalidateAsync(repository, userId, row.Ref, destination, cancellationToken);
		}

		private static void apply(OutboxMessage row, ResultEvent result)
		{
			row.State = OutboxState.Completed;
			row.CompletedAt = result.CompletedAt.UtcDateTime;
			row.ResultId = result.ResultId;
			row.ResultStatus = truncate(result.Status, 32);
			row.ErrorKind = truncate(result.ErrorKind, 32);
			row.ErrorCode = truncate(result.ErrorCode, 128);
			row.ErrorMessage = truncate(result.ErrorMessage, 1000);
			row.ProviderMessageId = truncate(result.ProviderMessageId, 256);
			row.DestinationInvalid = result.DestinationInvalid;
			row.Attempts = result.Attempts;
			row.Envelope = null;
		}

		private static string? truncate(string? value, int maxLength) => value is { } && value.Length > maxLength ? value[..maxLength] : value;
	}
}
