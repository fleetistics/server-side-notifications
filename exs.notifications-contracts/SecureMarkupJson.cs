using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace exs.notifications_contracts
{
	/// <summary>
	/// <see cref="SecureMarkup"/> for a message body that is JSON: every string value in it (a subject, a text, an HTML part,
	/// a push title, a data value) is checked, stripped or masked, so the senders need no knowledge of which fields a channel
	/// has. Property names are never touched.
	///
	/// A JSON writer escapes '&lt;' (as <), so the tags do not appear as such in the raw text of a body. Everything here
	/// therefore works on the parsed values, and the quick "is there anything to do" test looks for the word, not the tag.
	/// </summary>
	public static class SecureMarkupJson
	{
		private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

		/// <summary>One entry per string value whose tags do not pair up, such as "text: 2 &lt;secure&gt; tag(s) are never closed". Empty when all is well. Never contains a value.</summary>
		public static IReadOnlyList<string> Validate(JsonElement body)
		{
			var problems = new List<string>();
			collect(body, "", problems);
			return problems;
		}

		/// <summary>The JSON with the tags removed from every string and what they enclose kept. This is what the provider is given.</summary>
		public static string Strip(string json) => transform(json, SecureMarkup.Strip);

		/// <summary>The JSON with every marked part replaced by XXXX. This is what is kept.</summary>
		public static string Mask(string json) => transform(json, SecureMarkup.Mask);

		private static string transform(string json, Func<string?, string?> change)
		{
			// The raw text has '<' escaped, so look for the word; anything that merely mentions it is parsed and comes back the same.
			if (string.IsNullOrEmpty(json) || !json.Contains("secure", StringComparison.OrdinalIgnoreCase)) return json;

			var node = JsonNode.Parse(json);
			var changed = false;
			rewrite(node, text =>
			{
				var result = change(text);
				changed |= !string.Equals(result, text, StringComparison.Ordinal);
				return result;
			});

			// Nothing was marked after all ("secure" was just a word): hand back the original text rather than a re-written equal one.
			return changed ? node!.ToJsonString(Readable) : json;
		}

		/// <summary>Changes the string values below the node, in place.</summary>
		private static void rewrite(JsonNode? node, Func<string?, string?> change)
		{
			switch (node)
			{
				case JsonObject obj:
					foreach (var key in obj.Select(p => p.Key).ToList())
					{
						var child = obj[key];
						if (child is JsonValue value && value.TryGetValue<string>(out var text)) obj[key] = JsonValue.Create(change(text));
						else rewrite(child, change);
					}
					break;
				case JsonArray array:
					for (var i = 0; i < array.Count; i++)
					{
						if (array[i] is JsonValue value && value.TryGetValue<string>(out var text)) array[i] = JsonValue.Create(change(text));
						else rewrite(array[i], change);
					}
					break;
			}
		}

		private static void collect(JsonElement element, string path, List<string> problems)
		{
			switch (element.ValueKind)
			{
				case JsonValueKind.String:
					var problem = SecureMarkup.Validate(element.GetString());
					if (problem != null) problems.Add($"{(path.Length == 0 ? "(body)" : path)}: {problem}");
					break;
				case JsonValueKind.Object:
					foreach (var property in element.EnumerateObject()) collect(property.Value, path.Length == 0 ? property.Name : $"{path}.{property.Name}", problems);
					break;
				case JsonValueKind.Array:
					var index = 0;
					foreach (var item in element.EnumerateArray()) collect(item, $"{path}[{index++}]", problems);
					break;
			}
		}
	}
}
