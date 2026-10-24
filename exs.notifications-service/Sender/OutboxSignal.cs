using System.Threading.Channels;

namespace exs.notifications_service.Sender
{
	/// <summary>
	/// "There may be outbox rows to submit." The fan-out pokes it once it has committed rows, and the relay
	/// sleeps on it, so a message goes out straight away instead of waiting out the poll interval. Pokes
	/// coalesce: one is remembered at most, which is enough since the relay re-queries the database anyway.
	/// </summary>
	public sealed class OutboxSignal
	{
		private readonly Channel<bool> mChannel = Channel.CreateBounded<bool>(
			new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

		public void Notify() => mChannel.Writer.TryWrite(true);

		/// <summary>Returns when poked, when the timeout elapses, or on cancellation. The caller checks its own token.</summary>
		public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
		{
			using var wakeUp = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			wakeUp.CancelAfter(timeout);
			try
			{
				if (await mChannel.Reader.WaitToReadAsync(wakeUp.Token))
				{
					mChannel.Reader.TryRead(out _);
				}
			}
			catch (OperationCanceledException)
			{
				// Timeout or shutdown - either way just stop waiting.
			}
		}
	}

	/// <summary>
	/// One OutboxSignal per channel, so each channel's relay sleeps on its own and a poke for pushes does not
	/// have to be passed to the email relay by hand. The fan-out does not know which channels wrote rows, so it
	/// wakes them all; a relay with nothing to do goes straight back to sleep.
	/// </summary>
	public sealed class OutboxSignals
	{
		private readonly System.Collections.Concurrent.ConcurrentDictionary<string, OutboxSignal> mSignals = new();

		public OutboxSignal For(string channel) => mSignals.GetOrAdd(channel, _ => new OutboxSignal());

		public void NotifyAll()
		{
			foreach (var signal in mSignals.Values) signal.Notify();
		}
	}
}
