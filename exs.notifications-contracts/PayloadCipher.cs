using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace exs.notifications_contracts
{
	/// <summary>
	/// Configuration of a <see cref="PayloadCipher"/>: a set of named keys and which one encrypts. Bound from
	/// "NotificationService:Encryption" in a host and "Sender:Encryption" in a sender. Leave it empty to store payloads as they are.
	/// </summary>
	public sealed class PayloadCipherOptions
	{
		/// <summary>The key that encrypts from now on. One of <see cref="Keys"/>.</summary>
		public string? CurrentKeyId { get; set; }

		/// <summary>
		/// Key id to key: 32 random bytes, Base64 (for example `openssl rand -base64 32`). An id is 1-32 letters, digits,
		/// '_' or '-'. Keys that no longer encrypt stay here for as long as rows written with them may exist, so they can still be
		/// read; a key can be dropped once none do.
		/// </summary>
		public Dictionary<string, string> Keys { get; set; } = new();
	}

	/// <summary>
	/// Encrypts the text of a queued message (the envelope in the host's outbox, the body in the sender's queue) while it
	/// waits, so a copy of the database, a backup or a stray SELECT does not show an address or a reset link. AES-256-GCM,
	/// a fresh random nonce for every value, and the row's own identity bound in as associated data, so a ciphertext cannot be
	/// copied onto another row and still open.
	///
	/// A protected value looks like `enc1.&lt;keyId&gt;.&lt;base64&gt;`. Anything without that prefix is read as it is: rows
	/// written before encryption was switched on stay readable, and a cipher with no key (see <see cref="Disabled"/>) leaves
	/// values as they are. A protected value read by a cipher that lacks its key throws, rather than handing back the ciphertext.
	///
	/// Pure and dependency-free, like the rest of this project: the same class is used by the hosts and the senders.
	/// </summary>
	public sealed class PayloadCipher
	{
		public const string Prefix = "enc1.";

		private const int KeyLength = 32;
		private const int NonceLength = 12;
		private const int TagLength = 16;

		private static readonly Regex KeyIdPattern = new("^[A-Za-z0-9_-]{1,32}$", RegexOptions.Compiled);

		private readonly Dictionary<string, byte[]> mKeys;
		private readonly string? mCurrentKeyId;

		/// <summary>A cipher with no key: stores everything as it is. What an unconfigured host or sender gets.</summary>
		public static PayloadCipher Disabled { get; } = new(null, new Dictionary<string, byte[]>());

		private PayloadCipher(string? currentKeyId, Dictionary<string, byte[]> keys)
		{
			mCurrentKeyId = currentKeyId;
			mKeys = keys;
		}

		/// <summary>True when values are encrypted on the way in. Values already encrypted can be read either way, as long as their key is known.</summary>
		public bool IsEnabled => mCurrentKeyId != null;

		/// <summary>No keys and no current key: <see cref="Disabled"/>. Anything half-set, or a key that is not 32 bytes of Base64, throws, so a mistake shows at startup and not at the first message.</summary>
		/// <exception cref="InvalidOperationException">The options are inconsistent; the message says which setting, never what the key was.</exception>
		public static PayloadCipher Create(PayloadCipherOptions? options)
		{
			if (options == null || (options.Keys.Count == 0 && string.IsNullOrWhiteSpace(options.CurrentKeyId))) return Disabled;

			if (options.Keys.Count == 0) throw new InvalidOperationException("Encryption: CurrentKeyId is set but Keys is empty.");
			if (string.IsNullOrWhiteSpace(options.CurrentKeyId)) throw new InvalidOperationException("Encryption: Keys are given but CurrentKeyId is not set; it names the key that encrypts.");

			var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
			foreach (var (id, value) in options.Keys)
			{
				if (!KeyIdPattern.IsMatch(id)) throw new InvalidOperationException($"Encryption: key id '{id}' must be 1-32 letters, digits, '_' or '-'.");

				byte[] bytes;
				try
				{
					bytes = Convert.FromBase64String(value ?? "");
				}
				catch (FormatException)
				{
					throw new InvalidOperationException($"Encryption: key '{id}' is not valid Base64.");
				}

				if (bytes.Length != KeyLength) throw new InvalidOperationException($"Encryption: key '{id}' must be {KeyLength} bytes (a Base64 string of 44 characters), not {bytes.Length}.");
				keys[id] = bytes;
			}

			if (!keys.ContainsKey(options.CurrentKeyId)) throw new InvalidOperationException($"Encryption: CurrentKeyId '{options.CurrentKeyId}' is not one of the Keys.");
			return new PayloadCipher(options.CurrentKeyId, keys);
		}

		/// <summary>What ties a stored value to its row: the message id and channel. Pass the same context to <see cref="Unprotect"/>.</summary>
		public static string ContextFor(Guid messageId, string channel) => $"{messageId:D}|{channel}";

		public static bool IsProtected(string? stored) => stored != null && stored.StartsWith(Prefix, StringComparison.Ordinal);

		/// <summary>The value encrypted with the current key, or as it is when this cipher is <see cref="Disabled"/>.</summary>
		public string Protect(string plaintext, string context)
		{
			ArgumentNullException.ThrowIfNull(plaintext);
			if (mCurrentKeyId == null) return plaintext;

			var plain = Encoding.UTF8.GetBytes(plaintext);
			var nonce = RandomNumberGenerator.GetBytes(NonceLength);
			var cipherText = new byte[plain.Length];
			var tag = new byte[TagLength];
			using (var aes = new AesGcm(mKeys[mCurrentKeyId], TagLength))
			{
				aes.Encrypt(nonce, plain, cipherText, tag, Encoding.UTF8.GetBytes(context));
			}

			var packed = new byte[NonceLength + TagLength + cipherText.Length];
			nonce.CopyTo(packed, 0);
			tag.CopyTo(packed, NonceLength);
			cipherText.CopyTo(packed, NonceLength + TagLength);
			return $"{Prefix}{mCurrentKeyId}.{Convert.ToBase64String(packed)}";
		}

		/// <summary>The original value. A value that was never protected comes back as it is.</summary>
		/// <exception cref="InvalidOperationException">The value was encrypted with a key this cipher does not have, or is malformed.</exception>
		/// <exception cref="CryptographicException">The value or its context was changed since it was written.</exception>
		public string Unprotect(string stored, string context)
		{
			ArgumentNullException.ThrowIfNull(stored);
			if (!IsProtected(stored)) return stored;

			var rest = stored.AsSpan(Prefix.Length);
			var dot = rest.IndexOf('.');
			if (dot <= 0) throw new InvalidOperationException("A stored payload is encrypted but malformed.");

			var keyId = rest[..dot].ToString();
			if (!mKeys.TryGetValue(keyId, out var key)) throw new InvalidOperationException($"A stored payload was encrypted with key '{keyId}', which is not configured.");

			byte[] packed;
			try
			{
				packed = Convert.FromBase64String(rest[(dot + 1)..].ToString());
			}
			catch (FormatException)
			{
				throw new InvalidOperationException("A stored payload is encrypted but malformed.");
			}

			if (packed.Length < NonceLength + TagLength) throw new InvalidOperationException("A stored payload is encrypted but malformed.");

			var nonce = packed.AsSpan(0, NonceLength);
			var tag = packed.AsSpan(NonceLength, TagLength);
			var cipherText = packed.AsSpan(NonceLength + TagLength);
			var plain = new byte[cipherText.Length];
			using (var aes = new AesGcm(key, TagLength))
			{
				aes.Decrypt(nonce, cipherText, tag, plain, Encoding.UTF8.GetBytes(context));
			}

			return Encoding.UTF8.GetString(plain);
		}
	}
}
