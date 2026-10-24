using exs.notifications_contracts;
using exs.notifications_model.Notifications;
using exs.notifications_service.Sender;
using exs.notifications_service.Tests.Sender.Support;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace exs.notifications_service.Tests.Sender
{
	public class FcmBodyFactoryTests
	{
		private static readonly FcmResolvedDestination Destination = new(UserId: 7, Token: "tok-1", Platform: FcmPlatforms.Android);

		private static Notification notification(string? title = "Title", string? body = "Body", string? payload = null, short typeId = 3, int? entityId = 55) =>
			new() { Id = 99, TypeId = typeId, EntityId = entityId, Title = title, Body = body, Payload = payload };

		private static FcmBody build(Notification n, ILogger? logger = null, FcmResolvedDestination? destination = null) =>
			FcmBodyFactory.Build(n, destination ?? Destination, logger ?? NullLogger.Instance);

		[Fact]
		public void TitleBodyAndDestination_AreCarriedOver()
		{
			var body = build(notification(), destination: new FcmResolvedDestination(7, "tok-9", FcmPlatforms.Ios));

			body.Title.ShouldBe("Title");
			body.Body.ShouldBe("Body");
			body.Destination.Token.ShouldBe("tok-9");
			body.Destination.Platform.ShouldBe(FcmPlatforms.Ios);
		}

		[Fact]
		public void NeitherTitleNorBody_IsHowADataOnlyNotificationIsStored()
		{
			var body = build(notification(title: null, body: ""));

			body.Title.ShouldBeNull();
			body.Body.ShouldBeNull();
			body.Data.ShouldNotBeNull();
		}

		[Fact]
		public void TypeIdAndEntityId_AreAlwaysInTheData()
		{
			var body = build(notification(typeId: 3, entityId: 55));

			body.Data!["TypeId"].ShouldBe("3");
			body.Data["EntityId"].ShouldBe("55");
		}

		[Fact]
		public void WithoutAnEntity_TheEntityIdKeyIsOmitted()
		{
			build(notification(entityId: null)).Data!.ContainsKey("EntityId").ShouldBeFalse();
		}

		[Fact]
		public void PayloadProperties_AreFlattenedIntoStrings()
		{
			var body = build(notification(payload: """{ "name": "x", "count": 3, "ok": true, "nested": { "a": [1, 2] }, "nothing": null }"""));

			body.Data!["name"].ShouldBe("x");           // a JSON string: its text, unquoted
			body.Data["count"].ShouldBe("3");            // anything else: its raw JSON
			body.Data["ok"].ShouldBe("true");
			body.Data["nested"].ShouldBe("""{ "a": [1, 2] }""");
			body.Data["nothing"].ShouldBe("null");
		}

		[Theory]
		[InlineData("TypeId")]
		[InlineData("typeid")]
		[InlineData("TYPEID")]
		public void APayloadKeyCannotShadowTheRealTypeId_InAnyCasing(string key)
		{
			var body = build(notification(typeId: 3, payload: $$"""{ "{{key}}": "999" }"""));

			body.Data!["TypeId"].ShouldBe("3");
			body.Data.Count(kv => kv.Key.Equals("typeid", StringComparison.OrdinalIgnoreCase)).ShouldBe(1);
		}

		[Fact]
		public void APayloadEntityId_IsReplacedByTheRealOne_AndKeptWhenThereIsNone()
		{
			build(notification(entityId: 55, payload: """{ "EntityId": "999" }""")).Data!["EntityId"].ShouldBe("55");
			// With no real entity the payload's own value is left alone, as the old pipeline did.
			build(notification(entityId: null, payload: """{ "EntityId": "999" }""")).Data!["EntityId"].ShouldBe("999");
		}

		[Fact]
		public void Badge_BecomesATypedField_AndStaysInTheDataToo()
		{
			var body = build(notification(payload: """{ "badge": 5 }"""));

			body.Badge.ShouldBe(5);
			body.Data!["badge"].ShouldBe("5");
		}

		[Fact]
		public void BadgeZero_IsKeptRatherThanBeingTreatedAsAbsent()
		{
			build(notification(payload: """{ "badge": 0 }""")).Badge.ShouldBe(0);
		}

		[Theory]
		[InlineData("""{ "badge": "many" }""")]
		[InlineData("""{ "badge": 1.5 }""")]
		[InlineData("""{ "other": 1 }""")]
		public void ABadgeThatIsNotAWholeNumber_OrIsAbsent_IsNull(string payload)
		{
			build(notification(payload: payload)).Badge.ShouldBeNull();
		}

		[Fact]
		public void PayloadKeysAreFoundWhateverTheirCasing()
		{
			var body = build(notification(payload: """{ "Badge": 2, "ANDROIDSOUND": "alert", "iosSound": "alert.caf" }"""));

			body.Badge.ShouldBe(2);
			body.Sound!.Android.ShouldBe("alert");
			body.Sound.Ios.ShouldBe("alert.caf");
		}

		[Fact]
		public void SoundsAreEachPlatformsOwn_AndAbsentWhenNeitherIsGiven()
		{
			build(notification(payload: """{ "androidSound": "alert" }""")).Sound!.Ios.ShouldBeNull();
			build(notification(payload: """{ "iosSound": "x.caf" }""")).Sound!.Android.ShouldBeNull();
			build(notification(payload: """{ "iosSound": "" }""")).Sound.ShouldBeNull();
			build(notification(payload: """{ "x": 1 }""")).Sound.ShouldBeNull();
		}

		[Fact]
		public void NoPayload_StillGivesTheTypeId()
		{
			var body = build(notification(payload: null, entityId: null));

			body.Data!.Count.ShouldBe(1);
			body.Data["TypeId"].ShouldBe("3");
		}

		[Fact]
		public void AMalformedPayload_IsIgnoredAndLogged_NotFatal()
		{
			var logger = new ListLogger<FcmBodyFactoryTests>();

			var body = build(notification(payload: "{ not json"), logger);

			body.Data!["TypeId"].ShouldBe("3");
			logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message.Contains("#99"));
		}

		[Theory]
		[InlineData("[1,2,3]")]
		[InlineData("\"just a string\"")]
		[InlineData("42")]
		public void APayloadThatIsNotAnObject_IsIgnored(string payload)
		{
			var body = build(notification(payload: payload, entityId: null));

			body.Data!.Keys.ShouldBe(["TypeId"]);
		}
	}
}
