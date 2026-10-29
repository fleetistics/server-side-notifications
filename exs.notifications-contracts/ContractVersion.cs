namespace exs.notifications_contracts
{
	/// <summary>
	/// Wire-contract version, carried as schemaVersion in every envelope and result event.
	///
	/// Within one version only additive changes are allowed: new optional fields, and new values for the
	/// string vocabularies (channels, platforms, statuses, error kinds). Readers ignore what they do not
	/// know. Anything else - removing or renaming a field, changing a type or the meaning of a value,
	/// making an optional field required - is a new version, served alongside the previous one under a
	/// new path / routing key.
	/// </summary>
	public static class ContractVersion
	{
		public const int Current = 1;
	}
}
