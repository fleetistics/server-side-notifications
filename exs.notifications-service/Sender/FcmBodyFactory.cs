using System.Text.Json;
using exs.notifications_contracts;
using exs.notifications_model.Notifications;
using Microsoft.Extensions.Logging;

namespace exs.notifications_service.Sender
{
	/// <summary>
	/// Builds the contract's FcmBody from a stored Notification. Before the senders were separated the FCM
	/// worker did this with the Payload and the keys inside it; that knowledge now lives on the host's side,
	/// where the Payload comes from, and the sender receives typed fields instead of conventions.
	/// </summary>
	internal static class FcmBodyFactory
	{
		public static FcmBody Build(Notification notification, FcmResolvedDestination destination, ILogger logger)
		{
			// Case-insensitive so that a "Badge" or "typeid" written by whatever produced the Payload is found
			// by the same lookups, and so a payload key can never sit beside TypeId/EntityId under another casing.
			var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			int? badge = null;
			string? androidSound = null;
			string? iosSound = null;

			if (!string.IsNullOrWhiteSpace(notification.Payload))
			{
				try
				{
					using var document = JsonDocument.Parse(notification.Payload);
					if (document.RootElement.ValueKind == JsonValueKind.Object)
					{
						foreach (var property in document.RootElement.EnumerateObject())
						{
							// FCM's data is a flat string map: anything that is not already a JSON string is
							// kept as its raw JSON text, so numbers, bools and nested objects survive intact.
							var value = property.Value.ValueKind == JsonValueKind.String
								? property.Value.GetString() ?? string.Empty
								: property.Value.GetRawText();
							data[property.Name] = value;

							// These three are consumed here as typed fields, and still left in the data as before:
							// the apps may read them from there too.
							if (isNamed(property, "badge") && int.TryParse(value, out var parsed)) badge = parsed;
							if (isNamed(property, "androidSound")) androidSound = value;
							if (isNamed(property, "iosSound")) iosSound = value;
						}
					}
				}
				catch (JsonException ex)
				{
					logger.LogWarning(ex, "Notification #{NotificationId} has a Payload that is not valid JSON; sending without it.", notification.Id);
				}
			}

			// Set last, replacing any payload key of the same name in any casing: clients route on these two.
			data.Remove("TypeId");
			data["TypeId"] = notification.TypeId.ToString();
			if (notification.EntityId.HasValue)
			{
				data.Remove("EntityId");
				data["EntityId"] = notification.EntityId.Value.ToString();
			}

			return new FcmBody
			{
				Destination = new FcmDestination { Token = destination.Token, Platform = destination.Platform },
				// Neither title nor body is how a data-only notification (CreateMobileDataNotification) is stored.
				Title = string.IsNullOrEmpty(notification.Title) ? null : notification.Title,
				Body = string.IsNullOrEmpty(notification.Body) ? null : notification.Body,
				Data = data,
				Sound = !string.IsNullOrEmpty(androidSound) || !string.IsNullOrEmpty(iosSound)
					? new FcmSound { Android = androidSound, Ios = iosSound }
					: null,
				Badge = badge,
			};
		}

		// Not an extension called NameEquals: JsonProperty has an instance method of that name, which is
		// case-sensitive and would win over any extension, silently defeating the point of this helper.
		private static bool isNamed(JsonProperty property, string name) =>
			property.Name.Equals(name, StringComparison.OrdinalIgnoreCase);
	}
}
