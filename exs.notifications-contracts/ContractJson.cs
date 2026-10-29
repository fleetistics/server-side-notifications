using System.Text.Json;
using System.Text.Json.Serialization;

namespace exs.notifications_contracts
{
	/// <summary>
	/// The one set of serializer settings both sides use, so a message written by a host is read the same
	/// way by a sender. Tolerant where the contract allows it to grow (unknown members are skipped),
	/// strict where a wrong shape should be rejected rather than guessed at (required members, numbers
	/// must be numbers).
	/// </summary>
	public static class ContractJson
	{
		public static readonly JsonSerializerOptions Options = create();

		public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

		public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

		private static JsonSerializerOptions create() => new(JsonSerializerDefaults.Web)
		{
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
			UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
			// The Web defaults let a quoted "3" be read as a number; a contract that says badge is an
			// integer should reject that.
			NumberHandling = JsonNumberHandling.Strict,
			// "required" only demands that a key is present, so "destination": null used to get through as a null
			// in a non-nullable property and blow up later, in code that trusted the type. With this, a null for
			// a property declared non-nullable is a JsonException at parse time. Which members may be null is
			// therefore part of the contract: it is whatever the records mark with "?". It covers properties only:
			// not the values inside a dictionary, not the generic body of the envelope, and not writing. The channel
			// validators check those themselves.
			RespectNullableAnnotations = true,
		};
	}
}
