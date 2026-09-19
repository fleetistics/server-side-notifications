using exs.fcm_sender.Fcm;
using exs.notifications_database;
using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Options;
using exs.dbContextCommons;

namespace exs.fcm_sender.core
{
	public static class DiHelper
	{
		public static void RegisterServices(IServiceCollection services, IConfiguration configuration)
		{
			services
				.AddDbServices<NotificationDatabaseContext>(configuration)
				.Configure<FcmSenderOptions>(configuration.GetSection("FcmSender"))
				.AddSingleton(sp =>
				{
					var options = sp.GetRequiredService<IOptions<FcmSenderOptions>>().Value;
					if (string.IsNullOrWhiteSpace(options.FirebaseCredentialsFile))
					{
						throw new InvalidOperationException("FcmSender:FirebaseCredentialsFile is not configured.");
					}

					// FromFile is marked obsolete pointing at a CredentialFactory API that doesn't
					// exist yet in the resolved Google.Apis.Auth version - nothing to switch to.
#pragma warning disable CS0618
					return FirebaseApp.Create(new AppOptions
					{
						Credential = GoogleCredential.FromFile(options.FirebaseCredentialsFile),
					});
#pragma warning restore CS0618
				})
				.AddSingleton<IFcmMessageSender, FcmMessageSender>()
				.AddSingleton<ResilientFcmSender>()
				.AddScoped<NotificationQueueProcessor>()
				.AddHostedService<Worker>()
				;
		}
	}
}
