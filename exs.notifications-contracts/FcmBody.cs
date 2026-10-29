namespace exs.notifications_contracts
{
	public sealed record FcmBody
	{
		public required FcmDestination Destination { get; init; }

		/// <summary>Title and Body both absent means a data-only message: nothing is shown, the app is just woken with Data.</summary>
		public string? Title { get; init; }

		public string? Body { get; init; }

		/// <summary>
		/// Flat string map, which is all FCM's data payload can carry. The host builds it in full, including
		/// whatever routing keys its clients expect; the sender passes it through untouched.
		/// </summary>
		public Dictionary<string, string>? Data { get; init; }

		public FcmSound? Sound { get; init; }

		/// <summary>iOS badge count. 0 is meaningful (clear the badge) and is distinct from absent (leave it alone).</summary>
		public int? Badge { get; init; }

		/// <summary>See MessagePriorities. Absent means the sender's default, which is high.</summary>
		public string? Priority { get; init; }
	}

	public sealed record FcmDestination
	{
		/// <summary>FCM registration token. It only works with the Firebase project (account) of the app that minted it.</summary>
		public required string Token { get; init; }

		/// <summary>See FcmPlatforms. Decides which platform-specific config the sender builds.</summary>
		public required string Platform { get; init; }
	}

	/// <summary>
	/// Per-platform sound, because the two platforms disagree on format, extension and location: Android
	/// takes a res/raw resource name without extension, iOS a bundled aiff/wav/caf file name with one.
	/// Absent means the platform default.
	/// </summary>
	public sealed record FcmSound
	{
		public string? Android { get; init; }

		public string? Ios { get; init; }
	}
}
