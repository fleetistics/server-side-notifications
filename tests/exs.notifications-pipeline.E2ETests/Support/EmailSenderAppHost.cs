using System.Collections.Concurrent;
using exs.Database.Commons.Interfaces;
using exs.notifications_model.Notifications;
using exs.notifications_service.Sender;
using exs.sender_commons.Clients;
using exs.sender_commons.Http;
using exs.sender_commons.Persistence;
using exs.sender_email;
using exs.sender_email.Email;
using MailKit.Net.Smtp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MimeKit;

namespace exs.notifications_pipeline.E2ETests.Support
{
	public sealed record SmtpCall(string Account, MimeMessage Message);

	/// <summary>Stands in for the SMTP server: records what would have been sent and answers as scripted.</summary>
	public sealed class FakeEmailGateway : IEmailGateway
	{
		private readonly ConcurrentQueue<SmtpCall> mCalls = new();
		private int mCount;

		public IReadOnlyList<SmtpCall> Calls => mCalls.ToArray();

		/// <summary>Receives the 1-based call number. Default: succeed with a queue id.</summary>
		public Func<int, SmtpCall, Task<string?>>? Behavior { get; set; }

		public Task<string?> SendAsync(string account, MimeMessage message, CancellationToken cancellationToken)
		{
			var call = new SmtpCall(account, message);
			mCalls.Enqueue(call);
			var number = Interlocked.Increment(ref mCount);
			return Behavior != null ? Behavior(number, call) : Task.FromResult<string?>($"250 2.0.0 queued as E2E{number}");
		}

		public static SmtpCommandException RecipientRefused(int status, string text) =>
			new(SmtpErrorCode.RecipientNotAccepted, (SmtpStatusCode)status, new MailboxAddress("", "someone@example.com"), text);
	}

	/// <summary>
	/// The real exs.sender-email app (HTTP endpoints, intake, queue, dispatcher, email channel, result log),
	/// running in-process on a test server with SQLite and a fake SMTP gateway. It knows one host, "host", whose
	/// API key is EmailSenderAppHost.ApiKey, with accounts noreply (its default), support (allowed) and
	/// restricted (exists, but not allowed to it).
	/// </summary>
	public sealed class EmailSenderAppHost : IAsyncDisposable
	{
		public const string ApiKey = "e2e-mail-key";

		private readonly string mDirectory = Path.Combine(Path.GetTempPath(), $"e2e-email-sender-{Guid.NewGuid():N}");
		private WebApplication? mApp;

		public FakeEmailGateway Gateway { get; } = new();

		public static async Task<EmailSenderAppHost> StartAsync()
		{
			var host = new EmailSenderAppHost();
			await host.startAsync();
			return host;
		}

		/// <summary>The handler that carries an HttpClient straight into the app, with no network involved.</summary>
		public HttpMessageHandler CreateHandler() => mApp!.GetTestServer().CreateHandler();

		private async Task startAsync()
		{
			Directory.CreateDirectory(mDirectory);

			var builder = WebApplication.CreateBuilder();
			builder.WebHost.UseTestServer();
			builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["Sender:RetryBaseDelayMilliseconds"] = "1",
				["Sender:PollIntervalSeconds"] = "60",
				["Sender:ResultSettleSeconds"] = "0", // the settle window is tested on its own; here results should be readable at once
				["Sender:MaxAttemptsPerMessage"] = "3",
				["Sender:Clients:host:ApiKeySha256"] = ApiKeyHasher.HashHex(ApiKey),
				["Sender:Clients:host:DefaultAccounts:email"] = "noreply",
				["Sender:Clients:host:AllowedAccounts:email:0"] = "support",
				["Email:Accounts:noreply:Host"] = "smtp.test",
				["Email:Accounts:noreply:FromAddress"] = "noreply@example.com",
				["Email:Accounts:support:Host"] = "smtp.test",
				["Email:Accounts:support:FromAddress"] = "support@example.com",
				["Email:Accounts:restricted:Host"] = "smtp.test",
				["Email:Accounts:restricted:FromAddress"] = "restricted@example.com",
			});
			builder.Services.AddEmailSender(builder.Configuration, database =>
				database.UseSqlite($"Data Source={Path.Combine(mDirectory, "sender.db")};Default Timeout=30;Pooling=False"));
			builder.Services.RemoveAll<IEmailGateway>();
			builder.Services.AddSingleton<IEmailGateway>(Gateway);

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

	/// <summary>A host's address book: the users it will email, by id.</summary>
	public sealed class TestEmailResolver : IEmailDestinationResolver
	{
		public Dictionary<int, (string Address, string? Name, string? Account)> Users { get; } = new();

		public Task<IReadOnlyList<EmailResolvedDestination>> ResolveAsync(IRepository repository, Notification notification, IReadOnlyCollection<int> userIds, CancellationToken cancellationToken) =>
			Task.FromResult<IReadOnlyList<EmailResolvedDestination>>(userIds.Where(Users.ContainsKey)
				.Select(id => new EmailResolvedDestination(id, Users[id].Address, Users[id].Name, Users[id].Account, $"u:{id}")).ToList());
	}

	public sealed class TestEmailInvalidator : IEmailAddressInvalidator
	{
		private readonly ConcurrentQueue<(int UserId, string? Ref, string Address)> mCalls = new();

		public IReadOnlyList<(int UserId, string? Ref, string Address)> Calls => mCalls.ToArray();

		public Task<bool> InvalidateAsync(IRepository repository, int userId, string? @ref, string address, CancellationToken cancellationToken)
		{
			mCalls.Enqueue((userId, @ref, address));
			return Task.FromResult(true);
		}
	}
}
