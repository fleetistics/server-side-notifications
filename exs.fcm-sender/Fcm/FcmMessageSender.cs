using exs.modelCommons.AppStructure;
using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using System.Text.Json;

namespace exs.fcm_sender.Fcm
{
	/// <summary>
	/// Thin wrapper around FirebaseAdmin's FirebaseMessaging client - a single send attempt,
	/// translated into an FcmSendResult classified as transient/permanent so callers (the retry
	/// layer, then the queue processor) never need to know about FirebaseMessagingException.
	/// </summary>
	public sealed class FcmMessageSender : IFcmMessageSender
	{
		public FcmMessageSender(FirebaseApp firebaseApp, ILogger<FcmMessageSender> logger)
		{
			mMessaging = FirebaseMessaging.GetMessaging(firebaseApp);
			mLogger = logger;
		}

		public async Task<FcmSendResult> SendAsync(string token, short platformId, string title, string body, short typeId, int? entityId, string payload, CancellationToken cancellationToken)
		{
			var message = BuildMessage(token, platformId, title, body, typeId, entityId, payload, mLogger);

			try
			{
				var messageId = await mMessaging.SendAsync(message, cancellationToken);
				return FcmSendResult.Ok(messageId);
			}
			catch (FirebaseMessagingException ex)
			{
				return FcmSendResult.Failed(Classify(ex.MessagingErrorCode), ex.MessagingErrorCode?.ToString() ?? ex.ErrorCode.ToString(), ex.Message);
			}
		}

		// Unregistered/InvalidArgument/SenderIdMismatch/ThirdPartyAuthError mean this token or
		// request will never succeed - retrying wastes a slot and delays the sessions that can
		// still be delivered. Everything else (including an unclassified null code) is assumed
		// transient: safer to retry a handful of times than to silently drop a real message.
		internal static FcmSendOutcome Classify(MessagingErrorCode? code) => code switch
		{
			MessagingErrorCode.Unregistered => FcmSendOutcome.PermanentError,
			MessagingErrorCode.InvalidArgument => FcmSendOutcome.PermanentError,
			MessagingErrorCode.SenderIdMismatch => FcmSendOutcome.PermanentError,
			MessagingErrorCode.ThirdPartyAuthError => FcmSendOutcome.PermanentError,
			_ => FcmSendOutcome.TransientError,
		};

		// Split out from SendAsync so message-shape logic (platform-conditional config, badge/sound
		// wiring, payload flattening) is testable without a real FirebaseApp/network call - only this
		// method's caller (SendAsync) touches mMessaging.
		//
		// Message.Token is marked obsolete in favor of Fid (Firebase Installation ID), but our
		// mobile clients (and SessionClientInfo.FCMToken) only ever report the classic FCM
		// registration token - there is no FID anywhere in this pipeline to switch to.
#pragma warning disable CS0618
		internal static Message BuildMessage(string token, short platformId, string title, string body, short typeId, int? entityId, string payload, ILogger logger)
		{
			var data = BuildData(typeId, entityId, payload, logger);

			// Payload can optionally carry "badge" (iOS badge count - Aps.Badge left unset/unchanged
			// when absent, since 0 is a meaningful "clear the badge" value distinct from "not sent")
			// plus separate "androidSound"/"iosSound" keys, since the two platforms disagree on sound
			// file format/extension/location (Android: a res/raw resource name, no extension; iOS: a
			// bundled AIFF/WAV/CAF filename with extension) - one shared key can't express both correctly.
			int? badge = data.TryGetValue("badge", out var badgeString) && int.TryParse(badgeString, out var badgeValue)
				? badgeValue
				: null;
			data.TryGetValue("androidSound", out var androidSound);
			data.TryGetValue("iosSound", out var iosSound);

			return new Message
			{
				Token = token,
				Notification = new FirebaseAdmin.Messaging.Notification
				{
					Title = title,
					Body = body,
				},
				Data = data,
				// Built per the session's actual platform rather than sending both unconditionally -
				// ClientDevicePlatform also has Windows, so "just send everything, FCM ignores the
				// mismatched block" would be relying on undocumented-to-us behavior for a platform
				// this pipeline doesn't even target yet. Without an explicit ApnsConfig, iOS would
				// still receive the alert but silently (no sound) - unlike Android, the generic
				// Notification block alone isn't enough there.
				Android = platformId == ClientDevicePlatform.Android
					? new AndroidConfig
					{
						Priority = Priority.High,
						Notification = !string.IsNullOrEmpty(androidSound)
							? new AndroidNotification { Sound = androidSound }
							: null,
					}
					: null,
				Apns = platformId == ClientDevicePlatform.Ios
					? new ApnsConfig
					{
						Headers = new Dictionary<string, string> { ["apns-priority"] = "10" },
						Aps = new Aps
						{
							// "default" plays the system notification sound - APNs requires some
							// sound value to actually play anything, so this is the fallback rather
							// than leaving Sound unset, unlike Android where no sound resource means
							// no custom Notification block at all.
							Sound = !string.IsNullOrEmpty(iosSound) ? iosSound : "default",
							Badge = badge,
							Alert = new ApsAlert
							{
								Title = title,
								Body = body,
							},
						},
					}
					: null,
			};
		}
#pragma warning restore CS0618

		// FCM's Data payload is flat string/string - Payload is stored as an arbitrary JSON object,
		// so its top-level properties are flattened into strings (GetRawText for anything that
		// isn't already a JSON string, so numbers/bools/nested objects survive round-trippable on
		// the client). TypeId/EntityId are set last so they can never be shadowed by a same-named
		// key inside Payload - the client relies on them to route the notification.
		internal static Dictionary<string, string> BuildData(short typeId, int? entityId, string payload, ILogger logger)
		{
			// Case-insensitive so a "Badge"/"AndroidSound" in Payload (whatever wrote it) is still
			// found by the exact-cased lookups above - doesn't affect what's actually sent as Data.
			var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

			if (!string.IsNullOrWhiteSpace(payload))
			{
				try
				{
					using var document = JsonDocument.Parse(payload);
					if (document.RootElement.ValueKind == JsonValueKind.Object)
					{
						foreach (var property in document.RootElement.EnumerateObject())
						{
							data[property.Name] = property.Value.ValueKind == JsonValueKind.String
								? property.Value.GetString() ?? string.Empty
								: property.Value.GetRawText();
						}
					}
				}
				catch (JsonException ex)
				{
					logger.LogWarning(ex, "Notification.Payload is not valid JSON, ignoring it: {Payload}", payload);
				}
			}

			data["TypeId"] = typeId.ToString();
			if (entityId.HasValue)
			{
				data["EntityId"] = entityId.Value.ToString();
			}

			return data;
		}

		private readonly FirebaseMessaging mMessaging;
		private readonly ILogger<FcmMessageSender> mLogger;
	}
}
