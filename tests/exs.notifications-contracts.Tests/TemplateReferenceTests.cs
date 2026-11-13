using System.Text.Json.Nodes;
using Shouldly;

namespace exs.notifications_contracts.Tests
{
	/// <summary>The optional template reference in the envelope: an additive field, so an envelope without it is unchanged and a reader that does not know it can skip it.</summary>
	public class TemplateReferenceTests
	{
		private static MessageEnvelope<EmailBody> Envelope(TemplateReference? template) => new()
		{
			SchemaVersion = ContractVersion.Current,
			MessageId = Guid.Parse("0b8f6d7e-5a3c-4c52-8d0e-2f6a9b1c3d4e"),
			Channel = Channels.Email,
			CreatedAt = new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero),
			ExpiresAt = new DateTimeOffset(2026, 10, 19, 9, 30, 0, TimeSpan.Zero),
			Template = template,
			Body = new EmailBody { To = new EmailAddress { Address = "sam@example.com" }, Subject = "S", Text = "T" },
		};

		[Fact]
		public void TheReferenceIsWrittenAsATemplateObject_WithLowerCamelNames()
		{
			var json = JsonNode.Parse(ContractJson.Serialize(Envelope(new TemplateReference { Key = "password-reset", Version = 3, Language = "uk" })))!;

			json["template"]!["key"]!.GetValue<string>().ShouldBe("password-reset");
			json["template"]!["version"]!.GetValue<int>().ShouldBe(3);
			json["template"]!["language"]!.GetValue<string>().ShouldBe("uk");
		}

		[Fact]
		public void WithoutAReference_TheEnvelopeIsExactlyAsBefore()
		{
			var json = JsonNode.Parse(ContractJson.Serialize(Envelope(null)))!.AsObject();

			json.ContainsKey("template").ShouldBeFalse();
		}

		[Fact]
		public void TheReferenceReadsBack()
		{
			var written = ContractJson.Serialize(Envelope(new TemplateReference { Key = "k", Version = 2, Language = "en" }));

			var read = ContractJson.Deserialize<MessageEnvelope<EmailBody>>(written)!;

			read.Template.ShouldBe(new TemplateReference { Key = "k", Version = 2, Language = "en" });
		}

		[Fact]
		public void ASenderThatDoesNotKnowTheFieldStillReadsTheEnvelope()
		{
			// An older reader is the same serializer without the property: simulated by reading the header as JSON and
			// asking only for what it knew.
			var json = ContractJson.Serialize(Envelope(new TemplateReference { Key = "k", Version = 1, Language = "en" }));

			var header = ContractJson.Deserialize<OldEnvelope>(json)!;

			header.MessageId.ShouldBe(Guid.Parse("0b8f6d7e-5a3c-4c52-8d0e-2f6a9b1c3d4e"));
			header.Channel.ShouldBe(Channels.Email);
		}

		[Fact]
		public void AReferenceWithAMissingPartIsRefusedWhenRead()
		{
			const string json = "{\"schemaVersion\":1,\"messageId\":\"0b8f6d7e-5a3c-4c52-8d0e-2f6a9b1c3d4e\",\"channel\":\"email\",\"createdAt\":\"2026-10-19T09:00:00+00:00\",\"expiresAt\":\"2026-10-19T09:30:00+00:00\",\"template\":{\"key\":\"k\"},\"body\":{\"to\":{\"address\":\"a@b.co\"},\"subject\":\"S\",\"text\":\"T\"}}";

			Should.Throw<System.Text.Json.JsonException>(() => ContractJson.Deserialize<MessageEnvelope<EmailBody>>(json));
		}

		/// <summary>What a receiver written before the field existed asks for.</summary>
		private sealed record OldEnvelope
		{
			public required int SchemaVersion { get; init; }

			public required Guid MessageId { get; init; }

			public required string Channel { get; init; }
		}
	}
}
