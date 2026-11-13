using System.Text;

namespace exs.notifications_contracts
{
	/// <summary>
	/// The `&lt;secure&gt;...&lt;/secure&gt;` markup that says which parts of a message must not outlive its sending: a
	/// reset link, a one-time code. The publisher writes the tags into the text it hands over; the sender takes them
	/// out before the provider sees the message (<see cref="Strip"/>) and stores the message with the marked parts
	/// replaced (<see cref="Mask"/>), so the record still tells what happened without keeping the secret.
	///
	/// The tags are plain text, matched without regard to case, and carry nothing: no attributes, no spaces.
	/// Tags may nest, and then the outermost pair decides what is masked. Everything here works on the string as it is,
	/// never on an HTML tree, so it behaves the same inside an attribute (`href="&lt;secure&gt;https://...&lt;/secure&gt;"`)
	/// and in a plain-text part.
	///
	/// Pure and dependency-free on purpose: the hosts use it too, to mask their own copy of a message.
	/// </summary>
	public static class SecureMarkup
	{
		public const string OpenTag = "<secure>";
		public const string CloseTag = "</secure>";

		/// <summary>What a marked part is replaced with in the stored copy. Fixed, so the length of a secret is not kept either.</summary>
		public const string MaskText = "XXXX";

		/// <summary>What <see cref="Neutralize"/> puts in place of the '&lt;' that would start a tag: a fullwidth look-alike that is not markup anywhere.</summary>
		private const char Lookalike = '＜';

		private const string OpenPrefix = "<secure";
		private const string ClosePrefix = "</secure";

		/// <summary>True when the text contains a tag at all. A cheap check before the other methods.</summary>
		public static bool HasTags(string? text) => text != null && find(text, 0, out _, out _);

		/// <summary>
		/// Null when the tags in the text are properly paired, otherwise a description of the first problem. The
		/// description never contains the text itself: it can be a secret.
		/// </summary>
		public static string? Validate(string? text)
		{
			if (string.IsNullOrEmpty(text)) return null;

			var depth = 0;
			var position = 0;
			while (find(text, position, out var tagStart, out var isOpen))
			{
				position = tagStart + (isOpen ? OpenTag.Length : CloseTag.Length);
				if (isOpen)
				{
					depth++;
				}
				else if (--depth < 0)
				{
					return $"a closing {CloseTag} at position {tagStart} has no matching {OpenTag}";
				}
			}

			return depth == 0 ? null : $"{depth} {OpenTag} tag(s) are never closed";
		}

		/// <summary>The text without the tags, with what they enclose kept. This is what is sent.</summary>
		public static string? Strip(string? text)
		{
			if (string.IsNullOrEmpty(text)) return text;

			var result = new StringBuilder(text.Length);
			var position = 0;
			while (find(text, position, out var tagStart, out var isOpen))
			{
				result.Append(text, position, tagStart - position);
				position = tagStart + (isOpen ? OpenTag.Length : CloseTag.Length);
			}

			result.Append(text, position, text.Length - position);
			return result.ToString();
		}

		/// <summary>
		/// The text with every marked part, tags included, replaced by XXXX. This is what is kept. Text that does not
		/// pair up is masked the safe way round rather than refused, because this runs after the message was sent
		/// and must never leave a secret behind: an opening tag with no closing one masks to the end of the text, and
		/// a closing tag with no opening one is simply dropped.
		/// </summary>
		public static string? Mask(string? text)
		{
			if (string.IsNullOrEmpty(text)) return text;

			var result = new StringBuilder(text.Length);
			var position = 0;
			var depth = 0;
			while (find(text, position, out var tagStart, out var isOpen))
			{
				if (depth == 0) result.Append(text, position, tagStart - position);
				position = tagStart + (isOpen ? OpenTag.Length : CloseTag.Length);

				if (isOpen)
				{
					if (depth++ == 0) result.Append(MaskText);
				}
				else if (depth > 0)
				{
					depth--;
				}
			}

			// An unclosed secret runs to the end of the text, and its mask is already in the result.
			if (depth == 0) result.Append(text, position, text.Length - position);
			return result.ToString();
		}

		/// <summary>
		/// For a value that is about to be placed between a template's own tags: anything in it that could pass for a tag
		/// is made harmless, so a value can neither close a secret early nor open one. A tag cannot be re-formed from
		/// what is left, because the '&lt;' itself is replaced.
		/// </summary>
		public static string Neutralize(string value)
		{
			if (string.IsNullOrEmpty(value) || value.IndexOf('<') < 0) return value;

			StringBuilder? result = null;
			for (var i = 0; i < value.Length; i++)
			{
				var isTagStart = value[i] == '<' && (startsWith(value, i, OpenPrefix) || startsWith(value, i, ClosePrefix));
				if (isTagStart && result == null) result = new StringBuilder(value, 0, i, value.Length);
				result?.Append(isTagStart ? Lookalike : value[i]);
			}

			return result?.ToString() ?? value;
		}

		/// <summary>The next tag at or after <paramref name="from"/>. A tag is exactly OpenTag or CloseTag.</summary>
		private static bool find(string text, int from, out int tagStart, out bool isOpen)
		{
			for (var i = text.IndexOf('<', from); i >= 0; i = text.IndexOf('<', i + 1))
			{
				if (startsWith(text, i, OpenTag))
				{
					tagStart = i;
					isOpen = true;
					return true;
				}

				if (startsWith(text, i, CloseTag))
				{
					tagStart = i;
					isOpen = false;
					return true;
				}
			}

			tagStart = -1;
			isOpen = false;
			return false;
		}

		private static bool startsWith(string text, int at, string value) => string.Compare(text, at, value, 0, value.Length, StringComparison.OrdinalIgnoreCase) == 0;
	}
}
