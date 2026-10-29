using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;

namespace exs.notifications_contracts.Tests
{
	/// <summary>
	/// Pins the wire format. The golden strings below are the contract as agreed: if one of these tests has
	/// to change, that is a contract change and needs a conscious decision about versioning (see
	/// ContractVersion), not just a test edit.
	/// </summary>
	public class ContractJsonTests
	{
		private const string GoldenFcmEnvelope = """
			{
			  "schemaVersion": 1,
			  "messageId": "6f1c2d0e-3b6a-4d5e-9c1f-0a1b2c3d4e5f",
			  "channel": "fcm",
			  "account": "mf5",
			  "createdAt": "2026-10-16T10:15:30Z",
			  "expiresAt": "2026-10-16T10:45:30Z",
			  "ref": "n:991|s:4821",
			  "body": {
			    "destination": { "token": "abc123", "platform": "android" },
			    "title": "Search Party Joined",
			    "body": "User 'Sam' joined",
			    "data": { "TypeId": "7", "EntityId": "55" },
			    "sound": { "android": "alert", "ios": "alert.caf" },
			    "badge": 3,
			    "priority": "high"
			  }
			}
			""";

		private const string GoldenResult = """
			{
			  "schemaVersion": 1,
			  "resultId": 10482,
			  "messageId": "6f1c2d0e-3b6a-4d5e-9c1f-0a1b2c3d4e5f",
			  "channel": "fcm",
			  "account": "mf5",
			  "ref": "n:991|s:4821",
			  "status": "failed",
			  "destination": "abc123",
			  "destinationInvalid": true,
			  "errorKind": "destination_invalid",
			  "errorCode": "Unregistered",
			  "errorMessage": "Requested entity was not found.",
			  "attempts": 1,
			  "completedAt": "2026-10-16T10:15:31Z"
			}
			""";

		private static MessageEnvelope<FcmBody> minimalEnvelope() => new()
		{
			SchemaVersion = ContractVersion.Current,
			MessageId = Guid.Parse("6f1c2d0e-3b6a-4d5e-9c1f-0a1b2c3d4e5f"),
			Channel = Channels.Fcm,
			CreatedAt = new DateTimeOffset(2026, 10, 16, 10, 15, 30, TimeSpan.Zero),
			ExpiresAt = new DateTimeOffset(2026, 10, 16, 10, 45, 30, TimeSpan.Zero),
			Body = new FcmBody
			{
				Destination = new FcmDestination { Token = "abc123", Platform = FcmPlatforms.Ios },
			},
		};

		public class FcmEnvelope
		{
			[Fact]
			public void GoldenJson_DeserializesEveryField()
			{
				var envelope = ContractJson.Deserialize<MessageEnvelope<FcmBody>>(GoldenFcmEnvelope)!;

				envelope.SchemaVersion.ShouldBe(ContractVersion.Current);
				envelope.MessageId.ShouldBe(Guid.Parse("6f1c2d0e-3b6a-4d5e-9c1f-0a1b2c3d4e5f"));
				envelope.Channel.ShouldBe(Channels.Fcm);
				envelope.Account.ShouldBe("mf5");
				envelope.CreatedAt.ShouldBe(new DateTimeOffset(2026, 10, 16, 10, 15, 30, TimeSpan.Zero));
				envelope.ExpiresAt.ShouldBe(new DateTimeOffset(2026, 10, 16, 10, 45, 30, TimeSpan.Zero));
				envelope.Ref.ShouldBe("n:991|s:4821");
				envelope.Body.Destination.Token.ShouldBe("abc123");
				envelope.Body.Destination.Platform.ShouldBe(FcmPlatforms.Android);
				envelope.Body.Title.ShouldBe("Search Party Joined");
				envelope.Body.Body.ShouldBe("User 'Sam' joined");
				envelope.Body.Data.ShouldNotBeNull();
				envelope.Body.Data["TypeId"].ShouldBe("7");
				envelope.Body.Data["EntityId"].ShouldBe("55");
				envelope.Body.Sound!.Android.ShouldBe("alert");
				envelope.Body.Sound.Ios.ShouldBe("alert.caf");
				envelope.Body.Badge.ShouldBe(3);
				envelope.Body.Priority.ShouldBe(MessagePriorities.High);
			}

			[Fact]
			public void RoundTrips()
			{
				var original = ContractJson.Deserialize<MessageEnvelope<FcmBody>>(GoldenFcmEnvelope)!;

				var copy = ContractJson.Deserialize<MessageEnvelope<FcmBody>>(ContractJson.Serialize(original))!;

				copy.MessageId.ShouldBe(original.MessageId);
				copy.Account.ShouldBe(original.Account);
				copy.Ref.ShouldBe(original.Ref);
				copy.CreatedAt.ShouldBe(original.CreatedAt);
				copy.Body.Title.ShouldBe(original.Body.Title);
				copy.Body.Data.ShouldBe(original.Body.Data);
				copy.Body.Sound!.Ios.ShouldBe(original.Body.Sound!.Ios);
				copy.Body.Badge.ShouldBe(original.Body.Badge);
			}

			[Fact]
			public void Serialize_UsesCamelCaseAndOmitsAbsentOptionals()
			{
				var json = JsonNode.Parse(ContractJson.Serialize(minimalEnvelope()))!.AsObject();

				json.ContainsKey("schemaVersion").ShouldBeTrue();
				json.ContainsKey("messageId").ShouldBeTrue();
				json.ContainsKey("account").ShouldBeFalse();
				json.ContainsKey("ref").ShouldBeFalse();

				var body = json["body"]!.AsObject();
				body.ContainsKey("destination").ShouldBeTrue();
				foreach (var absent in new[] { "title", "body", "data", "sound", "badge", "priority" })
				{
					body.ContainsKey(absent).ShouldBeFalse(absent);
				}
			}

			[Fact]
			public void BadgeZero_IsWrittenNotOmitted()
			{
				// 0 means "clear the badge" and must stay distinct from "badge absent".
				var envelope = minimalEnvelope() with { Body = minimalEnvelope().Body with { Badge = 0 } };

				var body = JsonNode.Parse(ContractJson.Serialize(envelope))!["body"]!.AsObject();

				body["badge"]!.GetValue<int>().ShouldBe(0);
			}

			[Fact]
			public void DataOnlyMessage_NeedsNoTitleOrBody()
			{
				var json = ContractJson.Serialize(minimalEnvelope());

				var envelope = ContractJson.Deserialize<MessageEnvelope<FcmBody>>(json)!;

				envelope.Body.Title.ShouldBeNull();
				envelope.Body.Body.ShouldBeNull();
			}

			[Fact]
			public void UnknownMembers_AreIgnored()
			{
				// Additive changes within a version must not break an older reader.
				var json = JsonNode.Parse(GoldenFcmEnvelope)!.AsObject();
				json["somethingNew"] = "x";
				json["body"]!["alsoNew"] = new JsonObject { ["nested"] = 1 };

				var envelope = ContractJson.Deserialize<MessageEnvelope<FcmBody>>(json.ToJsonString())!;

				envelope.Body.Destination.Token.ShouldBe("abc123");
			}

			[Theory]
			[InlineData("schemaVersion")]
			[InlineData("messageId")]
			[InlineData("channel")]
			[InlineData("createdAt")]
			[InlineData("expiresAt")]
			[InlineData("body")]
			public void MissingRequiredMember_IsRejected(string member)
			{
				var json = JsonNode.Parse(GoldenFcmEnvelope)!.AsObject();
				json.Remove(member);

				Should.Throw<JsonException>(() => ContractJson.Deserialize<MessageEnvelope<FcmBody>>(json.ToJsonString()));
			}

			[Theory]
			[InlineData("token")]
			[InlineData("platform")]
			public void MissingRequiredDestinationMember_IsRejected(string member)
			{
				var json = JsonNode.Parse(GoldenFcmEnvelope)!.AsObject();
				json["body"]!["destination"]!.AsObject().Remove(member);

				Should.Throw<JsonException>(() => ContractJson.Deserialize<MessageEnvelope<FcmBody>>(json.ToJsonString()));
			}

			[Fact]
			public void QuotedNumber_IsRejected()
			{
				var json = JsonNode.Parse(GoldenFcmEnvelope)!.AsObject();
				json["body"]!["badge"] = "3";

				Should.Throw<JsonException>(() => ContractJson.Deserialize<MessageEnvelope<FcmBody>>(json.ToJsonString()));
			}

			[Fact]
			public void UnknownVocabularyValues_AreKept()
			{
				// Vocabularies are strings so a value added later reaches an older reader intact.
				var json = JsonNode.Parse(GoldenFcmEnvelope)!.AsObject();
				json["channel"] = "carrier-pigeon";
				json["body"]!["destination"]!["platform"] = "windows";
				json["body"]!["priority"] = "urgent";

				var envelope = ContractJson.Deserialize<MessageEnvelope<FcmBody>>(json.ToJsonString())!;

				envelope.Channel.ShouldBe("carrier-pigeon");
				envelope.Body.Destination.Platform.ShouldBe("windows");
				envelope.Body.Priority.ShouldBe("urgent");
			}

			[Fact]
			public void ChannelCanBeReadWithoutKnowingTheBody()
			{
				// How an intake that serves several channels works: read the header first, decode the body once the channel is known.
				var envelope = ContractJson.Deserialize<MessageEnvelope<JsonElement>>(GoldenFcmEnvelope)!;

				envelope.Channel.ShouldBe(Channels.Fcm);
				envelope.Body.GetProperty("title").GetString().ShouldBe("Search Party Joined");

				var body = envelope.Body.Deserialize<FcmBody>(ContractJson.Options)!;
				body.Destination.Token.ShouldBe("abc123");
			}
		}

		public class HttpShapes
		{
			[Fact]
			public void IntakeResponse_GoldenJson_Deserializes()
			{
				var response = ContractJson.Deserialize<IntakeResponse>(
					"""{ "messageId": "6f1c2d0e-3b6a-4d5e-9c1f-0a1b2c3d4e5f", "status": "duplicate" }""")!;

				response.MessageId.ShouldBe(Guid.Parse("6f1c2d0e-3b6a-4d5e-9c1f-0a1b2c3d4e5f"));
				response.Status.ShouldBe(IntakeStatuses.Duplicate);
			}

			[Fact]
			public void ErrorResponse_GoldenJson_Deserializes()
			{
				var response = ContractJson.Deserialize<ErrorResponse>(
					"""{ "errors": [ { "code": "unknown_account", "message": "No such account." }, { "code": "invalid_body", "message": "token is required" } ] }""")!;

				response.Errors.Count.ShouldBe(2);
				response.Errors[0].Code.ShouldBe(IntakeErrorCodes.UnknownAccount);
				response.Errors[1].Message.ShouldBe("token is required");
			}

			[Fact]
			public void ResultPage_RoundTrips_AndWrapsTheResultsWithTheCursor()
			{
				var result = ContractJson.Deserialize<ResultEvent>(GoldenResult)!;
				var page = new ResultPage { Results = [result], NextAfter = result.ResultId };

				var json = JsonNode.Parse(ContractJson.Serialize(page))!.AsObject();
				json["nextAfter"]!.GetValue<long>().ShouldBe(10482);
				json["results"]!.AsArray().Count.ShouldBe(1);

				var copy = ContractJson.Deserialize<ResultPage>(json.ToJsonString())!;
				copy.Results.Single().ShouldBe(result);
				copy.NextAfter.ShouldBe(10482);
			}

			[Fact]
			public void ResultPage_MissingCursor_IsRejected()
			{
				Should.Throw<JsonException>(() => ContractJson.Deserialize<ResultPage>("""{ "results": [] }"""));
			}
		}

		public class Result
		{
			[Fact]
			public void GoldenJson_DeserializesEveryField()
			{
				var result = ContractJson.Deserialize<ResultEvent>(GoldenResult)!;

				result.SchemaVersion.ShouldBe(ContractVersion.Current);
				result.ResultId.ShouldBe(10482);
				result.MessageId.ShouldBe(Guid.Parse("6f1c2d0e-3b6a-4d5e-9c1f-0a1b2c3d4e5f"));
				result.Channel.ShouldBe(Channels.Fcm);
				result.Account.ShouldBe("mf5");
				result.Ref.ShouldBe("n:991|s:4821");
				result.Status.ShouldBe(ResultStatuses.Failed);
				result.Destination.ShouldBe("abc123");
				result.DestinationInvalid.ShouldBeTrue();
				result.ErrorKind.ShouldBe(ErrorKinds.DestinationInvalid);
				result.ErrorCode.ShouldBe("Unregistered");
				result.ErrorMessage.ShouldBe("Requested entity was not found.");
				result.Attempts.ShouldBe(1);
				result.CompletedAt.ShouldBe(new DateTimeOffset(2026, 10, 16, 10, 15, 31, TimeSpan.Zero));
			}

			[Fact]
			public void RoundTrips()
			{
				var original = ContractJson.Deserialize<ResultEvent>(GoldenResult)!;

				var copy = ContractJson.Deserialize<ResultEvent>(ContractJson.Serialize(original))!;

				copy.ShouldBe(original);
			}

			[Fact]
			public void SuccessfulResult_OmitsErrorFields()
			{
				var result = new ResultEvent
				{
					SchemaVersion = ContractVersion.Current,
					ResultId = 1,
					MessageId = Guid.NewGuid(),
					Channel = Channels.Fcm,
					Status = ResultStatuses.Sent,
					ProviderMessageId = "projects/p/messages/0:1",
					Attempts = 2,
					CompletedAt = DateTimeOffset.UtcNow,
				};

				var json = JsonNode.Parse(ContractJson.Serialize(result))!.AsObject();

				foreach (var absent in new[] { "errorKind", "errorCode", "errorMessage", "account", "ref", "destination" })
				{
					json.ContainsKey(absent).ShouldBeFalse(absent);
				}
				json["providerMessageId"]!.GetValue<string>().ShouldBe("projects/p/messages/0:1");
				json["destinationInvalid"]!.GetValue<bool>().ShouldBeFalse();
			}

			[Fact]
			public void DestinationInvalid_DefaultsToFalseWhenOmitted()
			{
				// A host must never clear a destination just because the field was missing.
				var json = JsonNode.Parse(GoldenResult)!.AsObject();
				json.Remove("destinationInvalid");

				var result = ContractJson.Deserialize<ResultEvent>(json.ToJsonString())!;

				result.DestinationInvalid.ShouldBeFalse();
			}

			[Fact]
			public void UnknownStatusAndErrorKind_AreKept()
			{
				var json = JsonNode.Parse(GoldenResult)!.AsObject();
				json["status"] = "queued";
				json["errorKind"] = "rate_limited";

				var result = ContractJson.Deserialize<ResultEvent>(json.ToJsonString())!;

				result.Status.ShouldBe("queued");
				result.ErrorKind.ShouldBe("rate_limited");
			}

			[Theory]
			[InlineData("schemaVersion")]
			[InlineData("resultId")]
			[InlineData("messageId")]
			[InlineData("channel")]
			[InlineData("status")]
			[InlineData("attempts")]
			[InlineData("completedAt")]
			public void MissingRequiredMember_IsRejected(string member)
			{
				var json = JsonNode.Parse(GoldenResult)!.AsObject();
				json.Remove(member);

				Should.Throw<JsonException>(() => ContractJson.Deserialize<ResultEvent>(json.ToJsonString()));
			}
		}
	}
}
