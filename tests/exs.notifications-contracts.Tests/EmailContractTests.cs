using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;

namespace exs.notifications_contracts.Tests
{
	/// <summary>Pins the email body's wire format the same way ContractJsonTests pins the push one.</summary>
	public class EmailContractTests
	{
		private const string GoldenEmailEnvelope = """
			{
			  "schemaVersion": 1,
			  "messageId": "0b8f6d7e-5a3c-4c52-8d0e-2f6a9b1c3d4e",
			  "channel": "email",
			  "account": "noreply",
			  "createdAt": "2026-10-19T09:00:00+00:00",
			  "expiresAt": "2026-10-19T09:30:00+00:00",
			  "ref": "n:12|u:7",
			  "body": {
			    "to": { "address": "sam@example.com", "name": "Sam Rivera" },
			    "replyTo": { "address": "support@example.com" },
			    "subject": "Your search party was updated",
			    "text": "Sam joined the party.",
			    "html": "<p>Sam joined the party.</p>",
			    "priority": "high"
			  }
			}
			""";

		[Fact]
		public void TheGoldenEmailEnvelope_ReadsToTheExpectedValues()
		{
			var envelope = ContractJson.Deserialize<MessageEnvelope<EmailBody>>(GoldenEmailEnvelope)!;

			envelope.Channel.ShouldBe(Channels.Email);
			envelope.Account.ShouldBe("noreply");
			envelope.Body.To.Address.ShouldBe("sam@example.com");
			envelope.Body.To.Name.ShouldBe("Sam Rivera");
			envelope.Body.ReplyTo!.Address.ShouldBe("support@example.com");
			envelope.Body.ReplyTo.Name.ShouldBeNull();
			envelope.Body.Subject.ShouldBe("Your search party was updated");
			envelope.Body.Text.ShouldBe("Sam joined the party.");
			envelope.Body.Html.ShouldBe("<p>Sam joined the party.</p>");
			envelope.Body.Priority.ShouldBe(MessagePriorities.High);
		}

		[Fact]
		public void WritingTheEnvelopeBack_ProducesTheGoldenJson()
		{
			var envelope = ContractJson.Deserialize<MessageEnvelope<EmailBody>>(GoldenEmailEnvelope)!;

			var written = JsonNode.Parse(ContractJson.Serialize(envelope))!;

			JsonNode.DeepEquals(written, JsonNode.Parse(GoldenEmailEnvelope)).ShouldBeTrue(written.ToJsonString());
		}

		[Fact]
		public void AbsentOptionalPartsAreLeftOutOfTheJson()
		{
			var body = new EmailBody { To = new EmailAddress { Address = "a@b.co" }, Subject = "S", Text = "T" };

			var json = JsonNode.Parse(ContractJson.Serialize(body))!.AsObject();

			json.Select(p => p.Key).OrderBy(k => k).ShouldBe(["subject", "text", "to"]);
			json["to"]!.AsObject().Select(p => p.Key).ShouldBe(["address"]);
		}

		[Fact]
		public void MembersAddedLaterAreSkippedByAnOlderReader()
		{
			const string json = """{ "to": { "address": "a@b.co", "verified": true }, "subject": "S", "text": "T", "cc": [ { "address": "c@d.co" } ] }""";

			var body = ContractJson.Deserialize<EmailBody>(json)!;

			body.To.Address.ShouldBe("a@b.co");
			body.Subject.ShouldBe("S");
		}

		[Fact]
		public void ABodyWithoutARecipientOrSubjectIsNotAnEmailBody()
		{
			Should.Throw<JsonException>(() => ContractJson.Deserialize<EmailBody>("""{ "subject": "S", "text": "T" }"""));
			Should.Throw<JsonException>(() => ContractJson.Deserialize<EmailBody>("""{ "to": { "address": "a@b.co" }, "text": "T" }"""));
		}
	}

	/// <summary>
	/// Which members may be null is part of the contract: exactly the ones the records mark with "?". A null for any
	/// other member is rejected when it is read, instead of sneaking through "required" as a null in a
	/// non-nullable property.
	/// </summary>
	public class NullabilityContractTests
	{
		[Theory]
		[InlineData("""{ "destination": null, "title": "t" }""")]
		[InlineData("""{ "destination": { "token": null, "platform": "android" } }""")]
		[InlineData("""{ "destination": { "token": "t", "platform": null } }""")]
		public void ANullInANonNullableFcmMember_IsRejectedWhenRead(string json)
		{
			Should.Throw<JsonException>(() => ContractJson.Deserialize<FcmBody>(json));
		}

		[Fact]
		public void ANullInAnOptionalFcmMember_IsFine()
		{
			var body = ContractJson.Deserialize<FcmBody>("""{ "destination": { "token": "t", "platform": "ios" }, "title": null, "body": null, "data": null, "sound": null, "badge": null, "priority": null }""")!;

			body.Title.ShouldBeNull();
			body.Data.ShouldBeNull();
			body.Badge.ShouldBeNull();
		}

		[Theory]
		[InlineData("""{ "to": null, "subject": "S" }""")]
		[InlineData("""{ "to": { "address": null }, "subject": "S" }""")]
		[InlineData("""{ "to": { "address": "a@b.co" }, "subject": null }""")]
		public void ANullInANonNullableEmailMember_IsRejectedWhenRead(string json)
		{
			Should.Throw<JsonException>(() => ContractJson.Deserialize<EmailBody>(json));
		}

		[Fact]
		public void ANullInAnOptionalEmailMember_IsFine()
		{
			var body = ContractJson.Deserialize<EmailBody>("""{ "to": { "address": "a@b.co", "name": null }, "replyTo": null, "subject": "S", "text": null, "html": "<p/>", "priority": null }""")!;

			body.To.Name.ShouldBeNull();
			body.ReplyTo.ShouldBeNull();
			body.Text.ShouldBeNull();
		}

		[Fact]
		public void ANullChannelInTheEnvelope_IsRejectedWhenRead()
		{
			const string json = """{ "schemaVersion": 1, "messageId": "6f1c2d0e-3b6a-4d5e-9c1f-0a1b2c3d4e5f", "channel": null, "createdAt": "2026-10-22T10:00:00Z", "expiresAt": "2026-10-22T10:30:00Z", "body": { } }""";

			Should.Throw<JsonException>(() => ContractJson.Deserialize<MessageEnvelope<FcmBody>>(json));
		}

		/// <summary>
		/// What the setting does NOT cover, pinned so nobody relies on it: values inside a dictionary and the
		/// generic body of the envelope are not checked, and writing is not checked. The channel validators
		/// still check those themselves (an empty body, a null data value).
		/// </summary>
		[Fact]
		public void TheSettingDoesNotReachInsideDictionariesOrTheEnvelopeBody()
		{
			Should.NotThrow(() => ContractJson.Deserialize<FcmBody>("""{ "destination": { "token": "t", "platform": "ios" }, "data": { "k": null } }"""));
			Should.NotThrow(() => ContractJson.Deserialize<MessageEnvelope<FcmBody>>("""{ "schemaVersion": 1, "messageId": "6f1c2d0e-3b6a-4d5e-9c1f-0a1b2c3d4e5f", "channel": "fcm", "createdAt": "2026-10-22T10:00:00Z", "expiresAt": "2026-10-22T10:30:00Z", "body": null }"""));
			Should.NotThrow(() => ContractJson.Serialize(new EmailBody { To = null!, Subject = "S" }));
		}

		[Fact]
		public void ANullInARequiredResultMember_IsRejectedWhenRead()
		{
			const string json = """{ "schemaVersion": 1, "resultId": 1, "messageId": "6f1c2d0e-3b6a-4d5e-9c1f-0a1b2c3d4e5f", "channel": "fcm", "status": null, "attempts": 1, "completedAt": "2026-10-22T10:00:00Z" }""";

			Should.Throw<JsonException>(() => ContractJson.Deserialize<ResultEvent>(json));
		}
	}
}
