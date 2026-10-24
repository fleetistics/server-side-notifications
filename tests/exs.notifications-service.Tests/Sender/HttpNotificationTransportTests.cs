using System.Net;
using System.Text;
using exs.notifications_contracts;
using exs.notifications_service.Sender;
using exs.notifications_service.Tests.Sender.Support;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace exs.notifications_service.Tests.Sender
{
	public class HttpNotificationTransportTests
	{
		private readonly StubHandler mHandler = new();
		private readonly ListLogger<HttpNotificationTransport> mLogger = new();

		private HttpNotificationTransport create() => new(new StubFactory(mHandler), mLogger);

		private static StringContent json(string text) => new(text, Encoding.UTF8, "application/json");

		public class Submitting
		{
			private readonly HttpNotificationTransportTests mOuter = new();

			[Fact]
			public async Task PostsTheEnvelopeAsJson_ToTheMessagesEndpoint()
			{
				mOuter.mHandler.Respond = _ => new HttpResponseMessage(HttpStatusCode.Accepted) { Content = json("""{ "messageId": "6f1c2d0e-3b6a-4d5e-9c1f-0a1b2c3d4e5f", "status": "accepted" }""") };

				await mOuter.create().SubmitAsync("""{ "the": "envelope" }""", CancellationToken.None);

				var request = mOuter.mHandler.Requests.ShouldHaveSingleItem();
				request.Method.ShouldBe(HttpMethod.Post);
				request.Uri.AbsolutePath.ShouldBe("/v1/messages");
				request.ContentType.ShouldBe("application/json");
				request.Body.ShouldBe("""{ "the": "envelope" }""");
			}

			[Fact]
			public async Task AcceptedAndDuplicate_AreBothSuccess_ButDistinguished()
			{
				mOuter.mHandler.Respond = _ => new HttpResponseMessage(HttpStatusCode.Accepted) { Content = json("""{ "messageId": "6f1c2d0e-3b6a-4d5e-9c1f-0a1b2c3d4e5f", "status": "accepted" }""") };
				(await mOuter.create().SubmitAsync("{}", CancellationToken.None)).Outcome.ShouldBe(SubmitOutcome.Accepted);

				mOuter.mHandler.Respond = _ => new HttpResponseMessage(HttpStatusCode.Accepted) { Content = json("""{ "messageId": "6f1c2d0e-3b6a-4d5e-9c1f-0a1b2c3d4e5f", "status": "duplicate" }""") };
				(await mOuter.create().SubmitAsync("{}", CancellationToken.None)).Outcome.ShouldBe(SubmitOutcome.Duplicate);
			}

			[Fact]
			public async Task A202WithAnUnreadableBody_IsStillAccepted()
			{
				mOuter.mHandler.Respond = _ => new HttpResponseMessage(HttpStatusCode.Accepted) { Content = json("not json at all") };

				(await mOuter.create().SubmitAsync("{}", CancellationToken.None)).Outcome.ShouldBe(SubmitOutcome.Accepted);
			}

			[Theory]
			[InlineData(HttpStatusCode.BadRequest)]
			[InlineData(HttpStatusCode.Forbidden)]
			[InlineData(HttpStatusCode.RequestEntityTooLarge)]
			[InlineData(HttpStatusCode.UnsupportedMediaType)]
			public async Task AnyOther4xx_IsTheSenderRefusingTheMessage_WithItsReasonsKept(HttpStatusCode status)
			{
				mOuter.mHandler.Respond = _ => new HttpResponseMessage(status)
				{
					Content = json("""{ "errors": [ { "code": "invalid_body", "message": "destination.token is required." }, { "code": "invalid_ref", "message": "ref is too long." } ] }"""),
				};

				var result = await mOuter.create().SubmitAsync("{}", CancellationToken.None);

				result.Outcome.ShouldBe(SubmitOutcome.Rejected);
				result.Detail.ShouldContain($"({(int)status})");
				result.Detail.ShouldContain("invalid_body: destination.token is required.");
				result.Detail.ShouldContain("invalid_ref: ref is too long.");
			}

			[Fact]
			public async Task ARefusalWithAnUnreadableBody_StillSaysSo_WithTheBodyCut()
			{
				mOuter.mHandler.Respond = _ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = json(new string('x', 1000)) };

				var result = await mOuter.create().SubmitAsync("{}", CancellationToken.None);

				result.Outcome.ShouldBe(SubmitOutcome.Rejected);
				result.Detail!.Length.ShouldBeLessThan(400);
			}

			[Fact]
			public async Task A401_IsNeverARejection_BecauseItIsThisHostsKeyAndNotTheMessage()
			{
				// Rejecting would burn every pending message for a fault one config change fixes.
				mOuter.mHandler.Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = json("""{ "errors": [ { "code": "unauthorized", "message": "A valid API key is required." } ] }""") };

				var result = await mOuter.create().SubmitAsync("{}", CancellationToken.None);

				result.Outcome.ShouldBe(SubmitOutcome.TransientFailure);
				result.Detail.ShouldContain("401");
				mOuter.mLogger.Entries.ShouldContain(e => e.Level == LogLevel.Error && e.Message.Contains("API key"));
			}

			[Theory]
			[InlineData(HttpStatusCode.InternalServerError)]
			[InlineData(HttpStatusCode.BadGateway)]
			[InlineData(HttpStatusCode.ServiceUnavailable)]
			[InlineData(HttpStatusCode.GatewayTimeout)]
			[InlineData(HttpStatusCode.RequestTimeout)]
			[InlineData(HttpStatusCode.TooManyRequests)]
			public async Task ServerErrorsAndBackPressure_AreTransient(HttpStatusCode status)
			{
				mOuter.mHandler.Respond = _ => new HttpResponseMessage(status);

				var result = await mOuter.create().SubmitAsync("{}", CancellationToken.None);

				result.Outcome.ShouldBe(SubmitOutcome.TransientFailure);
				result.Detail.ShouldContain(((int)status).ToString());
			}

			[Fact]
			public async Task AnUnreachableSender_IsTransient()
			{
				mOuter.mHandler.Respond = _ => throw new HttpRequestException("Connection refused");

				var result = await mOuter.create().SubmitAsync("{}", CancellationToken.None);

				result.Outcome.ShouldBe(SubmitOutcome.TransientFailure);
				result.Detail.ShouldContain("Connection refused");
			}

			[Fact]
			public async Task ATimeout_IsTransient()
			{
				mOuter.mHandler.Respond = _ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");

				(await mOuter.create().SubmitAsync("{}", CancellationToken.None)).Outcome.ShouldBe(SubmitOutcome.TransientFailure);
			}

			[Fact]
			public async Task ShutdownCancellation_IsNotMistakenForATimeout()
			{
				using var cancelled = new CancellationTokenSource();
				cancelled.Cancel();
				mOuter.mHandler.Respond = _ => throw new TaskCanceledException();

				await Should.ThrowAsync<OperationCanceledException>(() => mOuter.create().SubmitAsync("{}", cancelled.Token));
			}
		}

		public class FetchingResults
		{
			private readonly HttpNotificationTransportTests mOuter = new();

			[Fact]
			public async Task AsksForTheNextPageAfterTheCursor_AndReadsIt()
			{
				mOuter.mHandler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
				{
					Content = json("""
						{ "results": [ { "schemaVersion": 1, "resultId": 11, "messageId": "6f1c2d0e-3b6a-4d5e-9c1f-0a1b2c3d4e5f", "channel": "fcm",
						    "status": "sent", "attempts": 1, "completedAt": "2026-10-17T12:00:00Z" } ], "nextAfter": 11 }
						"""),
				};

				var page = await mOuter.create().FetchResultsAsync(5, 200, CancellationToken.None);

				var request = mOuter.mHandler.Requests.ShouldHaveSingleItem();
				request.Method.ShouldBe(HttpMethod.Get);
				request.Uri.PathAndQuery.ShouldBe("/v1/results?after=5&limit=200");
				page.NextAfter.ShouldBe(11);
				page.Results.Single().ResultId.ShouldBe(11);
				page.Results.Single().Status.ShouldBe(ResultStatuses.Sent);
			}

			[Theory]
			[InlineData(HttpStatusCode.Unauthorized)]
			[InlineData(HttpStatusCode.InternalServerError)]
			public async Task ANonSuccessStatus_IsAnExceptionCarryingIt_SoThePullerBacksOff(HttpStatusCode status)
			{
				mOuter.mHandler.Respond = _ => new HttpResponseMessage(status);

				var failure = await Should.ThrowAsync<HttpRequestException>(() => mOuter.create().FetchResultsAsync(0, 10, CancellationToken.None));

				failure.StatusCode.ShouldBe(status);
			}

			[Fact]
			public async Task AnEmptyBody_IsAnError_NotAnEmptyPage()
			{
				mOuter.mHandler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = json("null") };

				await Should.ThrowAsync<InvalidOperationException>(() => mOuter.create().FetchResultsAsync(0, 10, CancellationToken.None));
			}
		}

		private sealed record Seen(HttpMethod Method, Uri Uri, string? ContentType, string? Body);

		private sealed class StubHandler : HttpMessageHandler
		{
			private readonly List<Seen> mRequests = new();

			public IReadOnlyList<Seen> Requests => mRequests;

			public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.Accepted);

			protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
			{
				cancellationToken.ThrowIfCancellationRequested();
				mRequests.Add(new Seen(
					request.Method,
					request.RequestUri!,
					request.Content?.Headers.ContentType?.MediaType,
					request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
				return Respond(request);
			}
		}

		private sealed class StubFactory : IHttpClientFactory
		{
			private readonly StubHandler mHandler;

			public StubFactory(StubHandler handler) => mHandler = handler;

			public HttpClient CreateClient(string name) => new(mHandler, disposeHandler: false) { BaseAddress = new Uri("http://sender.test/") };
		}
	}
}
