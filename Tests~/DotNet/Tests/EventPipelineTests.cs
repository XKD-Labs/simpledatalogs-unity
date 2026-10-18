using System;
using System.IO;
using System.Linq;

using NUnit.Framework;

using XKDLabs.SimpleDataLogs.Core;

namespace XKDLabs.SimpleDataLogs.Tests
{
    /// <summary>The send state machine against a scripted transport and a fake clock.</summary>
    [TestFixture]
    public sealed class EventPipelineTests
    {
        /// <summary>The outbox folder for each test.</summary>
        private TemporaryDirectory storage;

        /// <summary>Records the SDK's log lines for each test.</summary>
        private RecordingLogSink logSink;

        /// <summary>The fake time.</summary>
        private FakeClock clock;

        /// <summary>The scripted transport.</summary>
        private FakeTransport transport;

        /// <summary>Makes a fresh folder, clock, transport and sink.</summary>
        [SetUp]
        public void SetUp()
        {
            storage = new TemporaryDirectory();
            logSink = new RecordingLogSink();
            SdkLog.Sink = logSink;
            clock = new FakeClock();
            transport = new FakeTransport();
        }

        /// <summary>Deletes the folder and silences the SDK.</summary>
        [TearDown]
        public void TearDown()
        {
            storage.Dispose();
            SdkLog.Sink = null;
        }

        /// <summary>A partial batch waits the batch interval, then goes out.</summary>
        [Test]
        public void PartialBatchesWaitTheBatchInterval()
        {
            EventPipeline pipeline = MakePipeline();
            pipeline.Track(TestKit.MakeEvent("tap", clock.UtcNow), clock.UtcNow);

            clock.Advance(TimeSpan.FromSeconds(29));
            pipeline.Tick(clock.UtcNow);
            Assert.That(transport.Requests, Is.Empty);

            clock.Advance(TimeSpan.FromSeconds(1));
            pipeline.Tick(clock.UtcNow);
            Assert.That(transport.Requests.Count, Is.EqualTo(1));
            Assert.That(pipeline.PendingEventCount, Is.EqualTo(0));
        }

        /// <summary>The 50th event seals the batch, and the next Tick sends it at once.</summary>
        [Test]
        public void FullBatchesGoOutAtOnce()
        {
            EventPipeline pipeline = MakePipeline();

            foreach (int eventNumber in Enumerable.Range(1, 120))
            {
                pipeline.Track(TestKit.MakeEvent("tap", clock.UtcNow), clock.UtcNow);
            }

            pipeline.Tick(clock.UtcNow);

            Assert.That(transport.Requests.Select(request => TestKit.EventIdsIn(request).Count), Is.EqualTo(new[] { 50, 50 }));
            Assert.That(pipeline.PendingEventCount, Is.EqualTo(20));
        }

        /// <summary>A request never passes the byte limit; the journal seals first.</summary>
        [Test]
        public void BatchesStayUnderTheByteLimit()
        {
            PipelineOptions options = TestKit.MakeOptions(storage.Path);
            options.MaxRequestBytes = 1000;
            EventPipeline pipeline = TestKit.MakeStartedPipeline(options, transport, clock);
            var bigProperties = new System.Collections.Generic.Dictionary<string, object> { ["text"] = new string('x', 300) };

            foreach (int eventNumber in Enumerable.Range(1, 7))
            {
                pipeline.Track(TestKit.MakeEvent("big", clock.UtcNow, properties: bigProperties), clock.UtcNow);
            }

            pipeline.Flush(clock.UtcNow, true);

            Assert.That(transport.Requests.All(request => TestKit.ReadBody(request).Length <= 1000), Is.True);
            Assert.That(transport.Requests.Sum(request => TestKit.EventIdsIn(request).Count), Is.EqualTo(7));
        }

        /// <summary>An event over the request limit on its own is dropped with a log line.</summary>
        [Test]
        public void OversizedEventsAreDropped()
        {
            PipelineOptions options = TestKit.MakeOptions(storage.Path);
            options.MaxRequestBytes = 200;
            EventPipeline pipeline = TestKit.MakeStartedPipeline(options, transport, clock);
            var hugeProperties = new System.Collections.Generic.Dictionary<string, object> { ["text"] = new string('x', 500) };

            pipeline.Track(TestKit.MakeEvent("huge", clock.UtcNow, properties: hugeProperties), clock.UtcNow);

            Assert.That(pipeline.PendingEventCount, Is.EqualTo(0));
            Assert.That(logSink.Contains("over the request limit"), Is.True);
        }

        /// <summary>Failures back off with jitter; nothing is sent before the backoff ends, and success resets the streak.</summary>
        [Test]
        public void FailuresBackOffUntilTheDelayEnds()
        {
            transport.Respond = FakeTransport.Script(IngestResponse.FromStatus(500, null), IngestResponse.NetworkFailure("offline"));
            EventPipeline pipeline = MakePipeline();
            pipeline.Track(TestKit.MakeEvent("tap", clock.UtcNow), clock.UtcNow);

            // First failure: ceiling 1 s, jitter 0.5, so 0.5 s.
            pipeline.Flush(clock.UtcNow, true);
            Assert.That(pipeline.BackoffUntilUtc, Is.EqualTo(clock.UtcNow.AddSeconds(0.5)));

            // Second failure: ceiling 2 s, so 1 s.
            clock.Advance(TimeSpan.FromSeconds(0.5));
            pipeline.Tick(clock.UtcNow);
            Assert.That(transport.Requests.Count, Is.EqualTo(2));
            Assert.That(pipeline.BackoffUntilUtc, Is.EqualTo(clock.UtcNow.AddSeconds(1)));

            // Nothing goes out during the backoff, not even on flush.
            clock.Advance(TimeSpan.FromSeconds(0.9));
            pipeline.Flush(clock.UtcNow, true);
            Assert.That(transport.Requests.Count, Is.EqualTo(2));

            // Then it succeeds and the streak resets.
            clock.Advance(TimeSpan.FromSeconds(0.1));
            pipeline.Tick(clock.UtcNow);
            Assert.That(transport.Requests.Count, Is.EqualTo(3));
            Assert.That(pipeline.ConsecutiveFailureCount, Is.EqualTo(0));
            Assert.That(pipeline.PendingEventCount, Is.EqualTo(0));
        }

        /// <summary>Retry-After pushes the retry later than the jittered backoff.</summary>
        [Test]
        public void RetryAfterPushesTheRetryLater()
        {
            transport.Respond = FakeTransport.Script(IngestResponse.FromStatus(503, "120"));
            EventPipeline pipeline = MakePipeline();
            pipeline.Track(TestKit.MakeEvent("tap", clock.UtcNow), clock.UtcNow);

            pipeline.Flush(clock.UtcNow, true);

            Assert.That(pipeline.BackoffUntilUtc, Is.EqualTo(clock.UtcNow.AddSeconds(120)));
        }

        /// <summary>Events tracked during a backoff stay in the journal instead of sealing one tiny file per interval.</summary>
        [Test]
        public void BackoffsDoNotSealTinyBatches()
        {
            transport.Respond = FakeTransport.Script(IngestResponse.FromStatus(503, "600"));
            EventPipeline pipeline = MakePipeline();
            pipeline.Track(TestKit.MakeEvent("tap", clock.UtcNow), clock.UtcNow);
            pipeline.Flush(clock.UtcNow, true);

            foreach (int minute in Enumerable.Range(1, 5))
            {
                pipeline.Track(TestKit.MakeEvent("tap", clock.UtcNow), clock.UtcNow);
                clock.Advance(TimeSpan.FromMinutes(1));
                pipeline.Tick(clock.UtcNow);
            }

            Assert.That(pipeline.Outbox.SealedBatches.Count, Is.EqualTo(1));
            Assert.That(pipeline.Outbox.JournalEventCount, Is.EqualTo(5));

            // When the backoff ends, the journal is sealed and both batches go.
            clock.Advance(TimeSpan.FromMinutes(6));
            pipeline.Tick(clock.UtcNow);
            Assert.That(pipeline.PendingEventCount, Is.EqualTo(0));
        }

        /// <summary>A 401 pauses sending, probes with the smallest batch after 60 s, then every 600 s, until a 202.</summary>
        [Test]
        public void InvalidKeysPauseAndProbe()
        {
            transport.Respond = FakeTransport.Script(
                IngestResponse.FromStatus(401, null),
                IngestResponse.FromStatus(401, null),
                IngestResponse.FromStatus(503, "900"));
            EventPipeline pipeline = MakePipeline();

            // Two batches: a big one and a small one.
            foreach (int eventNumber in Enumerable.Range(1, 50))
            {
                pipeline.Track(TestKit.MakeEvent("big", clock.UtcNow), clock.UtcNow);
            }

            pipeline.Track(TestKit.MakeEvent("small", clock.UtcNow), clock.UtcNow);
            pipeline.Flush(clock.UtcNow, true);

            Assert.That(pipeline.IsSendingPaused, Is.True);
            Assert.That(pipeline.NextProbeAtUtc, Is.EqualTo(clock.UtcNow.AddSeconds(60)));
            Assert.That(logSink.Count("rejected the key (401)"), Is.EqualTo(1));

            // Nothing goes out before the probe time.
            clock.Advance(TimeSpan.FromSeconds(59));
            pipeline.Flush(clock.UtcNow, true);
            Assert.That(transport.Requests.Count, Is.EqualTo(1));

            // The probe sends the one-event batch and gets 401 again: next probe in 600 s, still one error line.
            clock.Advance(TimeSpan.FromSeconds(1));
            pipeline.Tick(clock.UtcNow);
            Assert.That(TestKit.EventIdsIn(transport.Requests[1]).Count, Is.EqualTo(1));
            Assert.That(pipeline.NextProbeAtUtc, Is.EqualTo(clock.UtcNow.AddSeconds(600)));
            Assert.That(logSink.Count("rejected the key (401)"), Is.EqualTo(1));

            // A probe answered 503 + Retry-After keeps the pause and honors the hint as a floor.
            clock.Advance(TimeSpan.FromSeconds(600));
            pipeline.Tick(clock.UtcNow);
            Assert.That(pipeline.IsSendingPaused, Is.True);
            Assert.That(pipeline.NextProbeAtUtc, Is.EqualTo(clock.UtcNow.AddSeconds(900)));

            // A 202 ends the pause and drains the rest at once.
            clock.Advance(TimeSpan.FromSeconds(900));
            pipeline.Tick(clock.UtcNow);
            Assert.That(pipeline.IsSendingPaused, Is.False);
            Assert.That(pipeline.PendingEventCount, Is.EqualTo(0));
            Assert.That(transport.Requests.Count, Is.EqualTo(5));
        }

        /// <summary>A 413 splits the batch and resends the halves at once; a lone event is dropped.</summary>
        [Test]
        public void PayloadTooLargeSplits()
        {
            transport.Respond = request => TestKit.EventIdsIn(request).Count > 2
                ? IngestResponse.FromStatus(413, null)
                : IngestResponse.FromStatus(202, null);
            EventPipeline pipeline = MakePipeline();

            foreach (int eventNumber in Enumerable.Range(1, 8))
            {
                pipeline.Track(TestKit.MakeEvent("tap", clock.UtcNow), clock.UtcNow);
            }

            pipeline.Flush(clock.UtcNow, true);
            TestKit.TickUntilIdle(pipeline, clock);

            Assert.That(transport.Requests.Select(request => TestKit.EventIdsIn(request).Count), Is.EqualTo(new[] { 8, 4, 2, 2, 4, 2, 2 }));
            Assert.That(pipeline.PendingEventCount, Is.EqualTo(0));
        }

        /// <summary>A 415 sets the batch aside as .unsupported, never resends it, and the next batch goes.</summary>
        [Test]
        public void UnsupportedBatchesAreSetAside()
        {
            transport.Respond = FakeTransport.Script(IngestResponse.FromStatus(415, null));
            EventPipeline pipeline = MakePipeline();
            pipeline.Track(TestKit.MakeEvent("first", clock.UtcNow), clock.UtcNow);
            pipeline.Flush(clock.UtcNow, true);
            pipeline.Track(TestKit.MakeEvent("second", clock.UtcNow), clock.UtcNow);
            pipeline.Flush(clock.UtcNow, true);

            Assert.That(transport.Requests.Count, Is.EqualTo(2));
            Assert.That(pipeline.PendingEventCount, Is.EqualTo(0));
            Assert.That(Directory.GetFiles(pipeline.Outbox.BatchesDirectory, "*.unsupported").Length, Is.EqualTo(1));
        }

        /// <summary>A batch that fails to read three times in a row is set aside, and the next one goes.</summary>
        [Test]
        public void UnreadableBatchesAreSetAsideAfterThreeTries()
        {
            EventPipeline pipeline = MakePipeline();
            pipeline.Track(TestKit.MakeEvent("broken", clock.UtcNow), clock.UtcNow);
            pipeline.Flush(clock.UtcNow, false);
            pipeline.Track(TestKit.MakeEvent("fine", clock.UtcNow), clock.UtcNow);
            pipeline.Flush(clock.UtcNow, false);

            string brokenBatchPath = pipeline.Outbox.OldestSealedBatch.FilePath;
            pipeline.Outbox.ReadFileBytes = filePath => filePath == brokenBatchPath ? throw new IOException("bad sector") : File.ReadAllBytes(filePath);

            // Two failed reads back off like failed sends.
            pipeline.Tick(clock.UtcNow);
            clock.Advance(TimeSpan.FromMinutes(10));
            pipeline.Tick(clock.UtcNow);
            Assert.That(transport.Requests, Is.Empty);

            // The third sets it aside, and the healthy batch goes at once.
            clock.Advance(TimeSpan.FromMinutes(10));
            pipeline.Tick(clock.UtcNow);
            Assert.That(transport.Requests.Count, Is.EqualTo(1));
            Assert.That(File.Exists(brokenBatchPath + EventOutbox.UnreadableFileExtension), Is.True);
            Assert.That(pipeline.PendingEventCount, Is.EqualTo(0));
        }

        /// <summary>Bodies of 1 KB or more are gzipped; smaller ones and gzip-off options are not.</summary>
        [Test]
        public void GzipsLargeBodiesOnly()
        {
            PipelineOptions options = TestKit.MakeOptions(storage.Path);
            EventPipeline pipeline = TestKit.MakeStartedPipeline(options, transport, clock);
            var mediumProperties = new System.Collections.Generic.Dictionary<string, object> { ["text"] = new string('x', 1100) };

            pipeline.Track(TestKit.MakeEvent("small", clock.UtcNow), clock.UtcNow);
            pipeline.Flush(clock.UtcNow, true);
            pipeline.Track(TestKit.MakeEvent("large", clock.UtcNow, properties: mediumProperties), clock.UtcNow);
            pipeline.Flush(clock.UtcNow, true);

            Assert.That(transport.Requests[0].GetHeader("Content-Encoding"), Is.Null);
            Assert.That(transport.Requests[1].GetHeader("Content-Encoding"), Is.EqualTo("gzip"));
            Assert.That(TestKit.ReadBody(transport.Requests[1]), Does.Contain("\"event\":\"large\""));

            // WebGL-style options send neither gzip nor a User-Agent.
            using var webStorage = new TemporaryDirectory();
            PipelineOptions webOptions = TestKit.MakeOptions(webStorage.Path);
            webOptions.ShouldGzip = false;
            webOptions.UserAgent = null;
            var webTransport = new FakeTransport();
            EventPipeline webPipeline = TestKit.MakeStartedPipeline(webOptions, webTransport, clock);
            webPipeline.Track(TestKit.MakeEvent("large", clock.UtcNow, properties: mediumProperties), clock.UtcNow);
            webPipeline.Flush(clock.UtcNow, true);

            Assert.That(webTransport.Requests[0].GetHeader("Content-Encoding"), Is.Null);
            Assert.That(webTransport.Requests[0].GetHeader("User-Agent"), Is.Null);
        }

        /// <summary>An opt-out deletes everything and ignores the answer to a request already in flight.</summary>
        [Test]
        public void OptOutDeletesEverythingAndIgnoresLateAnswers()
        {
            transport.ShouldHoldAnswers = true;
            transport.Respond = request => IngestResponse.FromStatus(401, null);
            EventPipeline pipeline = MakePipeline();
            pipeline.Track(TestKit.MakeEvent("tap", clock.UtcNow), clock.UtcNow);
            pipeline.Flush(clock.UtcNow, true);
            pipeline.Track(TestKit.MakeEvent("tap", clock.UtcNow), clock.UtcNow);

            pipeline.SetEnabled(false);
            transport.ReleaseAnswers();
            pipeline.Tick(clock.UtcNow);

            Assert.That(pipeline.PendingEventCount, Is.EqualTo(0));
            Assert.That(pipeline.IsSendingPaused, Is.False);
            Assert.That(Directory.GetFiles(pipeline.Outbox.BatchesDirectory), Is.Empty);

            // Tracking while disabled does nothing; enabling again collects again.
            pipeline.Track(TestKit.MakeEvent("ignored", clock.UtcNow), clock.UtcNow);
            Assert.That(pipeline.PendingEventCount, Is.EqualTo(0));

            pipeline.SetEnabled(true);
            pipeline.Track(TestKit.MakeEvent("kept", clock.UtcNow), clock.UtcNow);
            Assert.That(pipeline.PendingEventCount, Is.EqualTo(1));
        }

        /// <summary>A transport that never answers is given up on after the timeout plus grace, and the batch is retried.</summary>
        [Test]
        public void MissingAnswersCountAsNetworkErrors()
        {
            transport.ShouldHoldAnswers = true;
            EventPipeline pipeline = MakePipeline();
            pipeline.Track(TestKit.MakeEvent("tap", clock.UtcNow), clock.UtcNow);
            pipeline.Flush(clock.UtcNow, true);

            clock.Advance(TimeSpan.FromSeconds(59));
            pipeline.Tick(clock.UtcNow);
            Assert.That(pipeline.IsRequestInFlight, Is.True);

            clock.Advance(TimeSpan.FromSeconds(1));
            pipeline.Tick(clock.UtcNow);
            Assert.That(pipeline.IsRequestInFlight, Is.False);
            Assert.That(pipeline.ConsecutiveFailureCount, Is.EqualTo(1));
            Assert.That(pipeline.PendingEventCount, Is.EqualTo(1));
        }

        /// <summary>A transport that throws counts as a network error.</summary>
        [Test]
        public void ThrowingTransportsCountAsNetworkErrors()
        {
            transport.Respond = request => throw new InvalidOperationException("boom");
            EventPipeline pipeline = MakePipeline();
            pipeline.Track(TestKit.MakeEvent("tap", clock.UtcNow), clock.UtcNow);

            Assert.DoesNotThrow(() => pipeline.Flush(clock.UtcNow, true));
            pipeline.Tick(clock.UtcNow);

            Assert.That(pipeline.ConsecutiveFailureCount, Is.EqualTo(1));
            Assert.That(pipeline.PendingEventCount, Is.EqualTo(1));
        }

        /// <summary>A relaunch resends the last launch's leftovers, and a disabled launch deletes them unsent.</summary>
        [Test]
        public void LaunchesResendOrDeleteLeftovers()
        {
            EventPipeline firstLaunch = MakePipeline();
            firstLaunch.Track(TestKit.MakeEvent("left", clock.UtcNow), clock.UtcNow);

            EventPipeline secondLaunch = MakePipeline();
            Assert.That(secondLaunch.PendingEventCount, Is.EqualTo(1));
            secondLaunch.Tick(clock.UtcNow);
            Assert.That(transport.Requests.Count, Is.EqualTo(1));

            firstLaunch.Track(TestKit.MakeEvent("left again", clock.UtcNow), clock.UtcNow);
            EventPipeline optedOutLaunch = TestKit.MakeStartedPipeline(TestKit.MakeOptions(storage.Path), transport, clock, isEnabled: false);
            Assert.That(optedOutLaunch.PendingEventCount, Is.EqualTo(0));
            Assert.That(File.Exists(optedOutLaunch.Outbox.JournalPath), Is.False);
        }

        // A started pipeline with production defaults.
        private EventPipeline MakePipeline()
        {
            return TestKit.MakeStartedPipeline(TestKit.MakeOptions(storage.Path), transport, clock);
        }
    }
}
