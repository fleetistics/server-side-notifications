using System.Collections.Concurrent;

namespace exs.notifications_service.Sender
{
	/// <summary>
	/// Keeps one outbox row whose processing threw out of the next polls for a while, longer after each
	/// repeat, so a row that cannot be processed does not sit at the head of the queue and crowd out the
	/// ones behind it. In memory on purpose: a restart forgets it, and the worst outcome is one prompt retry.
	/// </summary>
	internal sealed class RowCooldown
	{
		private readonly ConcurrentDictionary<long, (int Faults, DateTimeOffset Until)> mEntries = new();
		private readonly TimeSpan mMax;
		private readonly TimeProvider mClock;

		public RowCooldown(TimeSpan max, TimeProvider clock)
		{
			mMax = max;
			mClock = clock;
		}

		public void RegisterFault(long id)
		{
			var now = mClock.GetUtcNow();
			mEntries.AddOrUpdate(id, _ => (1, now + delay(1)), (_, previous) => (previous.Faults + 1, now + delay(previous.Faults + 1)));
		}

		public void Clear(long id) => mEntries.TryRemove(id, out _);

		public IReadOnlyCollection<long> CoolingIds()
		{
			var now = mClock.GetUtcNow();
			return mEntries.Where(e => e.Value.Until > now).Select(e => e.Key).ToList();
		}

		public TimeSpan? TimeUntilNextExpiry()
		{
			var now = mClock.GetUtcNow();
			var upcoming = mEntries.Values.Select(e => e.Until).Where(until => until > now).ToList();
			return upcoming.Count == 0 ? null : upcoming.Min() - now;
		}

		private TimeSpan delay(int faults)
		{
			var delay = TimeSpan.FromSeconds(2 * Math.Pow(2, Math.Min(faults - 1, 20)));
			return delay > mMax ? mMax : delay;
		}
	}

	/// <summary>
	/// A pause on all submissions after the sender itself fails (down, slow, or refusing this host's key).
	/// Without it every queued row would try the sender in turn and wait out its own timeout, so a backlog of
	/// a hundred messages would spend minutes confirming something the first failure already showed.
	/// </summary>
	internal sealed class TransportGate
	{
		private readonly object mLock = new();
		private readonly TimeSpan mMax;
		private readonly TimeProvider mClock;
		private int mFailures;
		private DateTimeOffset mBlockedUntil;

		public TransportGate(TimeSpan max, TimeProvider clock)
		{
			mMax = max;
			mClock = clock;
		}

		public bool IsBlocked => TimeUntilOpen() != null;

		public void RegisterFailure()
		{
			lock (mLock)
			{
				mFailures++;
				var delay = TimeSpan.FromSeconds(2 * Math.Pow(2, Math.Min(mFailures - 1, 20)));
				mBlockedUntil = mClock.GetUtcNow() + (delay > mMax ? mMax : delay);
			}
		}

		public void RegisterSuccess()
		{
			lock (mLock)
			{
				mFailures = 0;
				mBlockedUntil = default;
			}
		}

		/// <summary>How long until submissions may resume, or null when they already may.</summary>
		public TimeSpan? TimeUntilOpen()
		{
			lock (mLock)
			{
				var remaining = mBlockedUntil - mClock.GetUtcNow();
				return remaining > TimeSpan.Zero ? remaining : null;
			}
		}
	}
}
