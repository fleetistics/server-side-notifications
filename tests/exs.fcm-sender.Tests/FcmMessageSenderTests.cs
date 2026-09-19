using exs.fcm_sender.Fcm;
using exs.modelCommons.AppStructure;
using FirebaseAdmin.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace exs.fcm_sender.Tests
{
	public class FcmMessageSenderTests
	{
		public class BuildData
		{
			[Fact]
			public void NoPayload_OnlyCarriesTypeId()
			{
				var data = FcmMessageSender.BuildData(typeId: 7, entityId: null, payload: "", NullLogger.Instance);

				data.ShouldHaveSingleItem();
				data["TypeId"].ShouldBe("7");
			}

			[Fact]
			public void WithEntityId_IncludesIt()
			{
				var data = FcmMessageSender.BuildData(typeId: 7, entityId: 42, payload: "", NullLogger.Instance);

				data["TypeId"].ShouldBe("7");
				data["EntityId"].ShouldBe("42");
			}

			[Fact]
			public void PayloadObject_FlattensStringAndNonStringValues()
			{
				var payload = """{"title":"custom","count":3,"active":true}""";

				var data = FcmMessageSender.BuildData(typeId: 1, entityId: null, payload, NullLogger.Instance);

				data["title"].ShouldBe("custom"); // JSON string -> GetString(), unquoted
				data["count"].ShouldBe("3"); // JSON number -> GetRawText()
				data["active"].ShouldBe("true"); // JSON bool -> GetRawText()
			}

			[Fact]
			public void PayloadContainingTypeIdOrEntityId_CannotShadowTheRealOnes()
			{
				var payload = """{"TypeId":"999","EntityId":"999"}""";

				var data = FcmMessageSender.BuildData(typeId: 7, entityId: 42, payload, NullLogger.Instance);

				data["TypeId"].ShouldBe("7");
				data["EntityId"].ShouldBe("42");
			}

			[Fact]
			public void MalformedJsonPayload_IsIgnoredAndLogged()
			{
				var logger = new ListLogger<FcmMessageSender>();

				var data = FcmMessageSender.BuildData(typeId: 7, entityId: null, payload: "{not json", logger);

				data.ShouldHaveSingleItem(); // just TypeId - the bad payload contributed nothing
				data["TypeId"].ShouldBe("7");
				logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning);
			}

			[Fact]
			public void NonObjectPayload_IsIgnored()
			{
				// A JSON array/scalar at the root isn't something we know how to flatten into
				// FCM's flat string/string Data - same "ignore, keep TypeId" behavior as malformed JSON.
				var data = FcmMessageSender.BuildData(typeId: 7, entityId: null, payload: "[1,2,3]", NullLogger.Instance);

				data.ShouldHaveSingleItem();
				data["TypeId"].ShouldBe("7");
			}

			[Fact]
			public void KeyLookup_IsCaseInsensitive()
			{
				var payload = """{"Badge":5}""";

				var data = FcmMessageSender.BuildData(typeId: 1, entityId: null, payload, NullLogger.Instance);

				data.TryGetValue("badge", out var value).ShouldBeTrue();
				value.ShouldBe("5");
			}
		}

		public class Classify
		{
			[Theory]
			[InlineData(MessagingErrorCode.Unregistered)]
			[InlineData(MessagingErrorCode.InvalidArgument)]
			[InlineData(MessagingErrorCode.SenderIdMismatch)]
			[InlineData(MessagingErrorCode.ThirdPartyAuthError)]
			public void KnownPermanentCodes_ClassifyAsPermanent(MessagingErrorCode code)
			{
				FcmMessageSender.Classify(code).ShouldBe(FcmSendOutcome.PermanentError);
			}

			[Theory]
			[InlineData(MessagingErrorCode.Unavailable)]
			[InlineData(MessagingErrorCode.Internal)]
			[InlineData(MessagingErrorCode.QuotaExceeded)]
			public void KnownTransientCodes_ClassifyAsTransient(MessagingErrorCode code)
			{
				FcmMessageSender.Classify(code).ShouldBe(FcmSendOutcome.TransientError);
			}

			[Fact]
			public void UnclassifiedNullCode_DefaultsToTransient()
			{
				// Safer to retry an error we don't recognize than to silently drop a real message.
				FcmMessageSender.Classify(null).ShouldBe(FcmSendOutcome.TransientError);
			}
		}

		public class BuildMessage
		{
			[Fact]
			public void AndroidPlatform_GetsAndroidConfigOnly()
			{
				var message = FcmMessageSender.BuildMessage(
					"token", ClientDevicePlatform.Android, "title", "body", typeId: 1, entityId: null, payload: "", NullLogger.Instance);

				message.Android.ShouldNotBeNull();
				message.Android.Priority.ShouldBe(Priority.High);
				message.Apns.ShouldBeNull();
			}

			[Fact]
			public void IosPlatform_GetsApnsConfigOnly()
			{
				var message = FcmMessageSender.BuildMessage(
					"token", ClientDevicePlatform.Ios, "title", "body", typeId: 1, entityId: null, payload: "", NullLogger.Instance);

				message.Apns.ShouldNotBeNull();
				message.Apns.Aps.Sound.ShouldBe("default");
				message.Apns.Headers!["apns-priority"].ShouldBe("10");
				message.Android.ShouldBeNull();
			}

			[Fact]
			public void UnrecognizedPlatform_GetsNeitherConfig()
			{
				var message = FcmMessageSender.BuildMessage(
					"token", ClientDevicePlatform.Windows, "title", "body", typeId: 1, entityId: null, payload: "", NullLogger.Instance);

				message.Android.ShouldBeNull();
				message.Apns.ShouldBeNull();
			}

			[Fact]
			public void BadgeInPayload_SetsApsBadge()
			{
				var message = FcmMessageSender.BuildMessage(
					"token", ClientDevicePlatform.Ios, "title", "body", typeId: 1, entityId: null,
					payload: """{"badge":5}""", NullLogger.Instance);

				message.Apns!.Aps.Badge.ShouldBe(5);
			}

			[Fact]
			public void BadgeZeroInPayload_ClearsTheBadgeRatherThanBeingTreatedAsAbsent()
			{
				var message = FcmMessageSender.BuildMessage(
					"token", ClientDevicePlatform.Ios, "title", "body", typeId: 1, entityId: null,
					payload: """{"badge":0}""", NullLogger.Instance);

				message.Apns!.Aps.Badge.ShouldBe(0);
			}

			[Fact]
			public void NoBadgeInPayload_LeavesApsBadgeUnset()
			{
				var message = FcmMessageSender.BuildMessage(
					"token", ClientDevicePlatform.Ios, "title", "body", typeId: 1, entityId: null, payload: "", NullLogger.Instance);

				message.Apns!.Aps.Badge.ShouldBeNull();
			}

			[Fact]
			public void AndroidSoundInPayload_SetsAndroidNotificationSound()
			{
				var message = FcmMessageSender.BuildMessage(
					"token", ClientDevicePlatform.Android, "title", "body", typeId: 1, entityId: null,
					payload: """{"androidSound":"alert"}""", NullLogger.Instance);

				message.Android!.Notification.ShouldNotBeNull();
				message.Android.Notification!.Sound.ShouldBe("alert");
			}

			[Fact]
			public void NoAndroidSoundInPayload_LeavesAndroidNotificationUnset()
			{
				var message = FcmMessageSender.BuildMessage(
					"token", ClientDevicePlatform.Android, "title", "body", typeId: 1, entityId: null, payload: "", NullLogger.Instance);

				message.Android!.Notification.ShouldBeNull();
			}

			[Fact]
			public void IosSoundInPayloadDoesNotAffectAndroidNotification()
			{
				// androidSound/iosSound are independent keys - an iosSound-only payload shouldn't
				// leak into the Android config.
				var message = FcmMessageSender.BuildMessage(
					"token", ClientDevicePlatform.Android, "title", "body", typeId: 1, entityId: null,
					payload: """{"iosSound":"alert.caf"}""", NullLogger.Instance);

				message.Android!.Notification.ShouldBeNull();
			}

			[Fact]
			public void IosSoundInPayload_OverridesIosDefaultSound()
			{
				var message = FcmMessageSender.BuildMessage(
					"token", ClientDevicePlatform.Ios, "title", "body", typeId: 1, entityId: null,
					payload: """{"iosSound":"alert.caf"}""", NullLogger.Instance);

				message.Apns!.Aps.Sound.ShouldBe("alert.caf");
			}

			[Fact]
			public void NoIosSoundInPayload_FallsBackToDefaultSound()
			{
				var message = FcmMessageSender.BuildMessage(
					"token", ClientDevicePlatform.Ios, "title", "body", typeId: 1, entityId: null, payload: "", NullLogger.Instance);

				message.Apns!.Aps.Sound.ShouldBe("default");
			}

			[Fact]
			public void AndroidSoundInPayloadDoesNotAffectIosSound()
			{
				var message = FcmMessageSender.BuildMessage(
					"token", ClientDevicePlatform.Ios, "title", "body", typeId: 1, entityId: null,
					payload: """{"androidSound":"alert"}""", NullLogger.Instance);

				message.Apns!.Aps.Sound.ShouldBe("default");
			}

			[Fact]
			public void DataNode_CarriesTokenAndTypeId()
			{
#pragma warning disable CS0618 // Message.Token - see FcmMessageSender remarks
				var message = FcmMessageSender.BuildMessage(
					"the-token", ClientDevicePlatform.Ios, "title", "body", typeId: 3, entityId: 9, payload: "", NullLogger.Instance);

				message.Token.ShouldBe("the-token");
#pragma warning restore CS0618
				message.Data!["TypeId"].ShouldBe("3");
				message.Data["EntityId"].ShouldBe("9");
			}
		}

		private sealed class ListLogger<T> : ILogger<T>
		{
			public List<(LogLevel Level, string Message)> Entries { get; } = [];

			public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

			public bool IsEnabled(LogLevel logLevel) => true;

			public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
			{
				Entries.Add((logLevel, formatter(state, exception)));
			}
		}
	}
}
