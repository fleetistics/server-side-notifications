namespace exs.notifications_model.Outbox
{
	/// <summary>
	/// How far into a sender's result log this host has read. One row per sender, so the puller resumes where
	/// it stopped after a restart instead of starting from the beginning of the log.
	/// </summary>
	public class TransportCursor
	{
		public string Name { get; set; } = "";

		public long Position { get; set; }

		public DateTime UpdatedAt { get; set; }
	}
}
