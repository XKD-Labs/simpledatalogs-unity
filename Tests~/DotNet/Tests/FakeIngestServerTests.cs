using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using NUnit.Framework;

using XKDLabs.SimpleDataLogs.Core;

namespace XKDLabs.SimpleDataLogs.Tests
{
    /// <summary>One request as the fake server saw it.</summary>
    internal sealed class ReceivedRequest
    {
        /// <summary>Header name to value, case-insensitive.</summary>
        public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The body, gunzipped when it was gzipped.</summary>
        public string Body { get; set; }

        /// <summary>The status the server answered.</summary>
        public int AnsweredStatus { get; set; }
    }

    /// <summary>A real HTTP server on 127.0.0.1 that answers a scripted list of statuses.</summary>
    internal sealed class FakeIngestServer : IDisposable
    {
        /// <summary>The listener behind the server.</summary>
        private readonly HttpListener httpListener = new HttpListener();

        /// <summary>Statuses and Retry-After values still to answer; 202 once empty.</summary>
        private readonly ConcurrentQueue<(int StatusCode, string RetryAfter)> scriptedAnswers = new ConcurrentQueue<(int, string)>();

        /// <summary>Stops the accept loop.</summary>
        private readonly CancellationTokenSource stopSource = new CancellationTokenSource();

        /// <summary>Starts listening on a free port.</summary>
        public FakeIngestServer()
        {
            int freePort = FindFreePort();
            BaseUrl = $"http://127.0.0.1:{freePort}";
            httpListener.Prefixes.Add(BaseUrl + "/");
            httpListener.Start();
            Task.Run(AcceptLoopAsync);
        }

        /// <summary>The server's endpoint.</summary>
        public string BaseUrl { get; }

        /// <summary>Every request so far, in arrival order.</summary>
        public ConcurrentQueue<ReceivedRequest> ReceivedRequests { get; } = new ConcurrentQueue<ReceivedRequest>();

        /// <summary>Queues answers for the next requests.</summary>
        public void Script(params (int StatusCode, string RetryAfter)[] answers)
        {
            foreach ((int StatusCode, string RetryAfter) answer in answers)
            {
                scriptedAnswers.Enqueue(answer);
            }
        }

        /// <summary>Stops the server.</summary>
        public void Dispose()
        {
            stopSource.Cancel();
            httpListener.Close();
        }

        // Answers requests until stopped.
        private async Task AcceptLoopAsync()
        {
            while (!stopSource.IsCancellationRequested)
            {
                HttpListenerContext listenerContext;
                try
                {
                    listenerContext = await httpListener.GetContextAsync();
                }
                catch (Exception) when (stopSource.IsCancellationRequested)
                {
                    return;
                }

                Answer(listenerContext);
            }
        }

        // Records one request and answers the next scripted status.
        private void Answer(HttpListenerContext listenerContext)
        {
            var receivedRequest = new ReceivedRequest();
            foreach (string headerName in listenerContext.Request.Headers.AllKeys)
            {
                receivedRequest.Headers[headerName] = listenerContext.Request.Headers[headerName];
            }

            // Read the raw body, and gunzip it when the client said so.
            using (var bodyStream = new MemoryStream())
            {
                listenerContext.Request.InputStream.CopyTo(bodyStream);
                byte[] rawBody = bodyStream.ToArray();
                bool isGzipped = receivedRequest.Headers.TryGetValue("Content-Encoding", out string contentEncoding) && contentEncoding == "gzip";
                receivedRequest.Body = Encoding.UTF8.GetString(isGzipped ? TestKit.Gunzip(rawBody) : rawBody);
            }

            // Answer with the next scripted status.
            (int StatusCode, string RetryAfter) answer = scriptedAnswers.TryDequeue(out var scriptedAnswer) ? scriptedAnswer : (202, null);
            receivedRequest.AnsweredStatus = answer.StatusCode;
            ReceivedRequests.Enqueue(receivedRequest);

            listenerContext.Response.StatusCode = answer.StatusCode;
            if (answer.RetryAfter != null)
            {
                listenerContext.Response.Headers["Retry-After"] = answer.RetryAfter;
            }

            listenerContext.Response.Close();
        }

        // Asks the OS for a free TCP port.
        private static int FindFreePort()
        {
            var probeListener = new TcpListener(IPAddress.Loopback, 0);
            probeListener.Start();
            int freePort = ((IPEndPoint)probeListener.LocalEndpoint).Port;
            probeListener.Stop();

            return freePort;
        }
    }

    /// <summary>A transport over HttpClient, standing in for UnityWebRequest outside Unity.</summary>
    internal sealed class HttpClientTransport : IIngestTransport
    {
        /// <summary>One client for every request.</summary>
        private static readonly HttpClient SharedHttpClient = new HttpClient();

        /// <summary>Posts the batch and reports the status, or a network failure, on a thread-pool thread.</summary>
        public void Send(IngestRequest request, Action<IngestResponse> onComplete)
        {
            var requestMessage = new HttpRequestMessage(HttpMethod.Post, request.Url) { Content = new ByteArrayContent(request.Body) };

            // Content headers go on the content; the rest on the request.
            foreach (KeyValuePair<string, string> header in request.Headers)
            {
                if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value))
                {
                    requestMessage.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(request.TimeoutSeconds));
            SharedHttpClient.SendAsync(requestMessage, timeoutSource.Token).ContinueWith(sendTask =>
            {
                if (sendTask.IsFaulted || sendTask.IsCanceled)
                {
                    onComplete(IngestResponse.NetworkFailure(sendTask.Exception?.GetBaseException().Message ?? "canceled"));
                    return;
                }

                HttpResponseMessage responseMessage = sendTask.Result;
                string retryAfter = responseMessage.Headers.TryGetValues("Retry-After", out IEnumerable<string> retryAfterValues) ? retryAfterValues.First() : null;
                onComplete(IngestResponse.FromStatus((int)responseMessage.StatusCode, retryAfter));
            });
        }
    }

    /// <summary>The whole send path over real HTTP: headers, gzip, and every scripted answer.</summary>
    [TestFixture]
    public sealed class FakeIngestServerTests
    {
        /// <summary>The fake ingest server.</summary>
        private FakeIngestServer server;

        /// <summary>The outbox folder.</summary>
        private TemporaryDirectory storage;

        /// <summary>The fake time; real HTTP still runs.</summary>
        private FakeClock clock;

        /// <summary>The pipeline under test.</summary>
        private EventPipeline pipeline;

        /// <summary>Starts the server and a pipeline pointed at it.</summary>
        [SetUp]
        public void SetUp()
        {
            server = new FakeIngestServer();
            storage = new TemporaryDirectory();
            clock = new FakeClock();
            SdkLog.Sink = new RecordingLogSink();

            PipelineOptions options = TestKit.MakeOptions(storage.Path, SdkInfo.BuildIngestUrl(server.BaseUrl + "/"));
            pipeline = TestKit.MakeStartedPipeline(options, new HttpClientTransport(), clock);
        }

        /// <summary>Stops the server and deletes the folder.</summary>
        [TearDown]
        public void TearDown()
        {
            server.Dispose();
            storage.Dispose();
            SdkLog.Sink = null;
        }

        /// <summary>A 202 carries every header, gzips a large body, and deletes the batch.</summary>
        [Test]
        public async Task AcceptedBatchesCarryEveryHeaderAndLeave()
        {
            var largeProperties = new Dictionary<string, object> { ["text"] = new string('x', 2000) };
            pipeline.Track(TestKit.MakeEvent("level_complete", clock.UtcNow, "fixed-id", largeProperties), clock.UtcNow);

            pipeline.Flush(clock.UtcNow, true);
            await TickUntilIdleAsync();

            ReceivedRequest receivedRequest = server.ReceivedRequests.Single();
            Assert.That(receivedRequest.Headers["Authorization"], Is.EqualTo("Bearer sdl_pub_test"));
            Assert.That(receivedRequest.Headers["Content-Type"], Is.EqualTo("application/x-ndjson"));
            Assert.That(receivedRequest.Headers["User-Agent"], Is.EqualTo("simpledatalogs-unity/" + SdkInfo.Version));
            Assert.That(receivedRequest.Headers["Content-Encoding"], Is.EqualTo("gzip"));
            Assert.That(JsonDocument.Parse(receivedRequest.Body.TrimEnd('\n')).RootElement.GetProperty("event_id").GetString(), Is.EqualTo("fixed-id"));
            Assert.That(pipeline.PendingEventCount, Is.EqualTo(0));
        }

        /// <summary>A 503 with Retry-After waits at least that long, and the retry sends the same event_id and ts.</summary>
        [Test]
        public async Task RetriesHonorRetryAfterAndKeepIdentity()
        {
            server.Script((503, "45"));
            pipeline.Track(TestKit.MakeEvent("purchase", clock.UtcNow), clock.UtcNow);

            pipeline.Flush(clock.UtcNow, true);
            await TickUntilIdleAsync();
            Assert.That(pipeline.BackoffUntilUtc, Is.EqualTo(clock.UtcNow.AddSeconds(45)));

            // Not a moment before Retry-After.
            clock.Advance(TimeSpan.FromSeconds(44));
            await TickUntilIdleAsync();
            Assert.That(server.ReceivedRequests.Count, Is.EqualTo(1));

            clock.Advance(TimeSpan.FromSeconds(1));
            await TickUntilIdleAsync();

            ReceivedRequest[] receivedRequests = server.ReceivedRequests.ToArray();
            Assert.That(receivedRequests.Length, Is.EqualTo(2));
            Assert.That(receivedRequests[1].Body, Is.EqualTo(receivedRequests[0].Body), "Same event_id and ts on retry");
            Assert.That(pipeline.PendingEventCount, Is.EqualTo(0));
        }

        /// <summary>A 401 pauses with events kept, and the probe 60 s later ends the pause on 202.</summary>
        [Test]
        public async Task InvalidKeysPauseThenProbe()
        {
            server.Script((401, null));
            pipeline.Track(TestKit.MakeEvent("tap", clock.UtcNow), clock.UtcNow);

            pipeline.Flush(clock.UtcNow, true);
            await TickUntilIdleAsync();
            Assert.That(pipeline.IsSendingPaused, Is.True);
            Assert.That(pipeline.PendingEventCount, Is.EqualTo(1));

            clock.Advance(TimeSpan.FromSeconds(60));
            await TickUntilIdleAsync();

            Assert.That(pipeline.IsSendingPaused, Is.False);
            Assert.That(pipeline.PendingEventCount, Is.EqualTo(0));
            Assert.That(server.ReceivedRequests.Count, Is.EqualTo(2));
        }

        /// <summary>A 413 splits the batch and each half is delivered.</summary>
        [Test]
        public async Task TooLargeBatchesSplit()
        {
            server.Script((413, null));
            foreach (int eventNumber in Enumerable.Range(1, 6))
            {
                pipeline.Track(TestKit.MakeEvent("tap", clock.UtcNow), clock.UtcNow);
            }

            pipeline.Flush(clock.UtcNow, true);
            await TickUntilIdleAsync();

            int[] eventCounts = server.ReceivedRequests.Select(request => request.Body.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length).ToArray();
            Assert.That(eventCounts, Is.EqualTo(new[] { 6, 3, 3 }));
            Assert.That(pipeline.PendingEventCount, Is.EqualTo(0));
        }

        /// <summary>A 415 sets the batch aside, and the next batch is delivered.</summary>
        [Test]
        public async Task UnsupportedBatchesAreSetAside()
        {
            server.Script((415, null));
            pipeline.Track(TestKit.MakeEvent("first", clock.UtcNow), clock.UtcNow);
            pipeline.Flush(clock.UtcNow, false);
            pipeline.Track(TestKit.MakeEvent("second", clock.UtcNow), clock.UtcNow);

            pipeline.Flush(clock.UtcNow, true);
            await TickUntilIdleAsync();

            Assert.That(server.ReceivedRequests.Select(request => request.AnsweredStatus), Is.EqualTo(new[] { 415, 202 }));
            Assert.That(Directory.GetFiles(pipeline.Outbox.BatchesDirectory, "*.unsupported").Length, Is.EqualTo(1));
            Assert.That(pipeline.PendingEventCount, Is.EqualTo(0));
        }

        /// <summary>A server that is down is a network error: the batch stays and backs off.</summary>
        [Test]
        public async Task UnreachableServersAreRetried()
        {
            server.Dispose();
            pipeline.Track(TestKit.MakeEvent("tap", clock.UtcNow), clock.UtcNow);

            pipeline.Flush(clock.UtcNow, true);
            await TickUntilIdleAsync();

            Assert.That(pipeline.ConsecutiveFailureCount, Is.EqualTo(1));
            Assert.That(pipeline.PendingEventCount, Is.EqualTo(1));
        }

        // Ticks while real requests are in flight, up to 10 s.
        private async Task TickUntilIdleAsync()
        {
            DateTime giveUpAtUtc = DateTime.UtcNow.AddSeconds(10);

            pipeline.Tick(clock.UtcNow);
            while (pipeline.IsRequestInFlight && DateTime.UtcNow < giveUpAtUtc)
            {
                await Task.Delay(5);
                pipeline.Tick(clock.UtcNow);
            }
        }
    }
}
