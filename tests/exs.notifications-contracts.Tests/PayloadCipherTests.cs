using System.Security.Cryptography;
using Shouldly;

namespace exs.notifications_contracts.Tests
{
	public class PayloadCipherTests
	{
		private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

		private static PayloadCipher Cipher(string currentKeyId = "k1", params string[] keyIds)
		{
			var options = new PayloadCipherOptions { CurrentKeyId = currentKeyId };
			foreach (var id in keyIds.Length == 0 ? [currentKeyId] : keyIds) options.Keys[id] = NewKey();
			return PayloadCipher.Create(options);
		}

		private static readonly string Context = PayloadCipher.ContextFor(Guid.Parse("0b8f6d7e-5a3c-4c52-8d0e-2f6a9b1c3d4e"), "email");

		[Fact]
		public void AProtectedValueIsNotThePlaintext_AndReadsBackExactly()
		{
			var cipher = Cipher();
			const string plain = "{\"to\":\"sam@example.com\",\"text\":\"code 123456 Олена\"}";

			var stored = cipher.Protect(plain, Context);

			stored.ShouldStartWith(PayloadCipher.Prefix);
			stored.ShouldNotContain("sam@example.com");
			PayloadCipher.IsProtected(stored).ShouldBeTrue();
			cipher.Unprotect(stored, Context).ShouldBe(plain);
		}

		[Fact]
		public void EveryValueGetsItsOwnNonce()
		{
			var cipher = Cipher();

			cipher.Protect("same", Context).ShouldNotBe(cipher.Protect("same", Context));
		}

		[Fact]
		public void AValueCannotBeMovedToAnotherRow()
		{
			var cipher = Cipher();
			var stored = cipher.Protect("secret", Context);

			Should.Throw<CryptographicException>(() => cipher.Unprotect(stored, PayloadCipher.ContextFor(Guid.NewGuid(), "email")));
			Should.Throw<CryptographicException>(() => cipher.Unprotect(stored, PayloadCipher.ContextFor(Guid.Parse("0b8f6d7e-5a3c-4c52-8d0e-2f6a9b1c3d4e"), "fcm")));
		}

		[Theory]
		[InlineData(0)]   // the nonce
		[InlineData(12)]  // the tag
		[InlineData(28)]  // the ciphertext
		public void ATamperedValueIsRefused(int offset)
		{
			var cipher = Cipher();
			var stored = cipher.Protect("secret that is long enough to have bytes at every offset", Context);
			var parts = stored.Split('.');
			var packed = Convert.FromBase64String(parts[2]);
			packed[offset] ^= 0x01;
			var tampered = $"{parts[0]}.{parts[1]}.{Convert.ToBase64String(packed)}";

			Should.Throw<CryptographicException>(() => cipher.Unprotect(tampered, Context));
		}

		[Fact]
		public void AnotherKeyCannotOpenIt()
		{
			var stored = Cipher("k1").Protect("secret", Context);

			// Same id, different key bytes.
			Should.Throw<CryptographicException>(() => Cipher("k1").Unprotect(stored, Context));
			// Unknown id.
			Should.Throw<InvalidOperationException>(() => Cipher("k2").Unprotect(stored, Context)).Message.ShouldContain("k1");
		}

		[Fact]
		public void AnOldKeyStillOpensWhatItWrote_AfterTheCurrentOneChanged()
		{
			var options = new PayloadCipherOptions { CurrentKeyId = "old" };
			options.Keys["old"] = NewKey();
			var before = PayloadCipher.Create(options).Protect("written under the old key", Context);

			options.Keys["new"] = NewKey();
			options.CurrentKeyId = "new";
			var after = PayloadCipher.Create(options);

			after.Unprotect(before, Context).ShouldBe("written under the old key");
			after.Protect("x", Context).ShouldStartWith("enc1.new.");
		}

		[Fact]
		public void AValueThatWasNeverProtectedIsReadAsItIs()
		{
			// Rows written before encryption was switched on.
			Cipher().Unprotect("{\"plain\":true}", Context).ShouldBe("{\"plain\":true}");
		}

		[Fact]
		public void ADisabledCipherStoresAsItIs_AndRefusesWhatItCannotOpen()
		{
			var disabled = PayloadCipher.Create(null);

			disabled.IsEnabled.ShouldBeFalse();
			disabled.Protect("plain", Context).ShouldBe("plain");
			disabled.Unprotect("plain", Context).ShouldBe("plain");
			PayloadCipher.Create(new PayloadCipherOptions()).IsEnabled.ShouldBeFalse();
			var stored = Cipher().Protect("secret", Context);
			Should.Throw<InvalidOperationException>(() => disabled.Unprotect(stored, Context));
		}

		[Theory]
		[InlineData("enc1.k1")]
		[InlineData("enc1..abc")]
		[InlineData("enc1.k1.not base64!")]
		[InlineData("enc1.k1.AAAA")]
		public void AMalformedProtectedValueIsRefused(string stored)
		{
			Should.Throw<InvalidOperationException>(() => Cipher("k1").Unprotect(stored, Context));
		}

		[Fact]
		public void BadConfigurationIsRefusedAtOnce_WithoutEchoingAKey()
		{
			var key = NewKey();
			void refuse(PayloadCipherOptions options, string mention)
			{
				var ex = Should.Throw<InvalidOperationException>(() => PayloadCipher.Create(options));
				ex.Message.ShouldContain(mention);
				ex.Message.ShouldNotContain(key);
			}

			refuse(new PayloadCipherOptions { CurrentKeyId = "k1" }, "Keys is empty");
			refuse(new PayloadCipherOptions { Keys = { ["k1"] = key } }, "CurrentKeyId");
			refuse(new PayloadCipherOptions { CurrentKeyId = "k2", Keys = { ["k1"] = key } }, "k2");
			refuse(new PayloadCipherOptions { CurrentKeyId = "k1", Keys = { ["k1"] = "not base64!" } }, "Base64");
			refuse(new PayloadCipherOptions { CurrentKeyId = "k1", Keys = { ["k1"] = Convert.ToBase64String(new byte[16]) } }, "32 bytes");
			refuse(new PayloadCipherOptions { CurrentKeyId = "bad.id", Keys = { ["bad.id"] = key } }, "bad.id");
		}
	}
}
