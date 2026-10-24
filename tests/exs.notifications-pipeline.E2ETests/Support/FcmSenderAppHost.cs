using System.Collections.Concurrent;
using System.Reflection;
using exs.sender_commons.Clients;
using exs.sender_commons.Http;
using exs.sender_commons.Persistence;
using exs.sender_fcm;
using exs.sender_fcm.Fcm;
using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace exs.notifications_pipeline.E2ETests.Support
{
	public sealed record GatewayCall(string Account, Message Message)
	{
#pragma warning disable CS0618 // Message.Token - the sender sends registration tokens, not FIDs
		public string Token => Message.Token;
#pragma warning restore CS0618
	}

	/// <summary>Stands in for Firebase itself: records what would have been sent and answers as scripted.</summary>
	public sealed class FakeFcmGateway : IFcmGateway
	{
		private readonly ConcurrentQueue<GatewayCall> mCalls = new();
		private int mCount;

		public IReadOnlyList<GatewayCall> Calls => mCalls.ToArray();

		/// <summary>Receives the 1-based call number. Default: succeed with a message id.</summary>
		public Func<int, GatewayCall, Task<string>>? Behavior { get; set; }

		public Task<string> SendAsync(string account, Message message, CancellationToken cancellationToken)
		{
			var call = new GatewayCall(account, message);
			mCalls.Enqueue(call);
			var number = Interlocked.Increment(ref mCount);
			return Behavior != null ? Behavior(number, call) : Task.FromResult($"projects/e2e/messages/{number}");
		}
	}

	/// <summary>FirebaseMessagingException has no public constructor, so real ones are built by reflection.</summary>
	public static class FirebaseExceptions
	{
		public static FirebaseMessagingException Create(MessagingErrorCode? code, ErrorCode errorCode = ErrorCode.Unknown, string message = "FCM said no.")
		{
			var constructor = typeof(FirebaseMessagingException)
				.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
				.Where(c => c.GetParameters().Any(p => p.ParameterType == typeof(ErrorCode)) && c.GetParameters().Any(p => p.ParameterType == typeof(string)))
				.OrderBy(c => c.GetParameters().Length)
				.First();

			var arguments = constructor.GetParameters().Select(p =>
				p.ParameterType == typeof(ErrorCode) ? (object?)errorCode :
				p.ParameterType == typeof(string) ? message :
				p.ParameterType == typeof(MessagingErrorCode?) ? code :
				p.HasDefaultValue ? p.DefaultValue :
				p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();

			return (FirebaseMessagingException)constructor.Invoke(arguments);
		}
	}

	/// <summary>
	/// The real exs.sender-fcm app (HTTP endpoints, intake, queue, dispatcher, FCM channel, result log),
	/// running in-process on a test server with SQLite and a fake Firebase. It knows one host, "host", whose
	/// API key is FcmSenderAppHost.ApiKey, with FCM accounts main (its default), other (allowed) and
	/// restricted (exists, but not allowed to it).
	/// </summary>
	public sealed class FcmSenderAppHost : IAsyncDisposable
	{
		public const string ApiKey = "e2e-host-key";

		private readonly string mDirectory = Path.Combine(Path.GetTempPath(), $"e2e-sender-{Guid.NewGuid():N}");
		private WebApplication? mApp;

		public FakeFcmGateway Gateway { get; } = new();

		public static async Task<FcmSenderAppHost> StartAsync()
		{
			var host = new FcmSenderAppHost();
			await host.startAsync();
			return host;
		}

		/// <summary>The handler that carries an HttpClient straight into the app, with no network involved.</summary>
		public HttpMessageHandler CreateHandler() => mApp!.GetTestServer().CreateHandler();

		private async Task startAsync()
		{
			Directory.CreateDirectory(mDirectory);
			var credentials = Path.Combine(mDirectory, "google.json");
			File.WriteAllText(credentials, "{}"); // only has to exist: the gateway is fake, so nothing parses it

			var builder = WebApplication.CreateBuilder();
			builder.WebHost.UseTestServer();
			builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["Sender:RetryBaseDelayMilliseconds"] = "1",
				["Sender:PollIntervalSeconds"] = "60",
				["Sender:ResultSettleSeconds"] = "0", // the settle window is tested on its own; here results should be readable at once
				["Sender:MaxAttemptsPerMessage"] = "3",
				["Sender:Clients:host:ApiKeySha256"] = ApiKeyHasher.HashHex(ApiKey),
				["Sender:Clients:host:DefaultAccounts:fcm"] = "main",
				["Sender:Clients:host:AllowedAccounts:fcm:0"] = "other",
				["Fcm:Accounts:main:CredentialsFile"] = credentials,
				["Fcm:Accounts:other:CredentialsFile"] = credentials,
				["Fcm:Accounts:restricted:CredentialsFile"] = credentials,
			});
			builder.Services.AddFcmSender(builder.Configuration, database =>
				database.UseSqlite($"Data Source={Path.Combine(mDirectory, "sender.db")};Default Timeout=30;Pooling=False"));
			builder.Services.RemoveAll<IFcmGateway>();
			builder.Services.AddSingleton<IFcmGateway>(Gateway);

			mApp = builder.Build();
			using (var scope = mApp.Services.CreateScope())
			{
				var database = scope.ServiceProvider.GetRequiredService<SenderDbContext>().Database;
				database.EnsureCreated();
				database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
			}
			mApp.MapSenderEndpoints();
			await mApp.StartAsync();
		}

		public async ValueTask DisposeAsync()
		{
			if (mApp != null)
			{
				try { await mApp.StopAsync(); } catch { /* shutting down a test */ }
				await mApp.DisposeAsync();
			}
			Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
			try { Directory.Delete(mDirectory, recursive: true); } catch { /* temp folder */ }
		}
	}

	/// <summary>
	/// The "network" between the host and the sender. Passes requests to the sender app, and can be told to
	/// act like the network instead: be down, or lose the answer to a request the sender did receive.
	/// </summary>
	public sealed class FlakyNetwork : DelegatingHandler
	{
		private int mLoseNextAnswers;

		public FlakyNetwork(HttpMessageHandler inner) : base(inner) { }

		/// <summary>Every request fails to connect.</summary>
		public volatile bool Down;

		/// <summary>The sender gets the next POST and handles it, but the host never sees the answer.</summary>
		public void LoseTheNextAnswer() => Interlocked.Increment(ref mLoseNextAnswers);

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			if (Down) throw new HttpRequestException("No route to host (simulated).");

			var response = await base.SendAsync(request, cancellationToken);

			if (request.Method == HttpMethod.Post && Interlocked.Decrement(ref mLoseNextAnswers) >= 0)
			{
				response.Dispose();
				throw new HttpRequestException("Connection reset by peer (simulated).");
			}
			if (mLoseNextAnswers < 0) Interlocked.Exchange(ref mLoseNextAnswers, 0);
			return response;
		}

		// The sender app owns the real handler; the HTTP client factory must not dispose it with this wrapper.
		protected override void Dispose(bool disposing) { }
	}
}
