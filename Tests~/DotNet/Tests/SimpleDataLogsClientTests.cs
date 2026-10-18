using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using NUnit.Framework;

using XKDLabs.SimpleDataLogs.Core;

namespace XKDLabs.SimpleDataLogs.Tests
{
    /// <summary>The engine behind the facade: setup rules, stamping, defaults and autocapture.</summary>
    [TestFixture]
    public sealed class SimpleDataLogsClientTests
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

        /// <summary>Track before Configure, and an empty name, are logged and dropped.</summary>
        [Test]
        public void DropsMisusedTracks()
        {
            SimpleDataLogsClient client = TestKit.MakeClient(clock);

            client.Track("early", null);
            Assert.That(logSink.Contains("Track(early) was called before Configure"), Is.True);

            Configure(client);
            client.Track("", null);
            client.TrackScreen(null, null);

            Assert.That(logSink.Contains("empty event name"), Is.True);
            Assert.That(logSink.Contains("empty screen name"), Is.True);
            Assert.That(client.Pipeline.PendingEventCount, Is.EqualTo(0));
        }

        /// <summary>A second Configure warns and keeps the first.</summary>
        [Test]
        public void SecondConfigureIsIgnored()
        {
            SimpleDataLogsClient client = TestKit.MakeClient(clock);

            Assert.That(Configure(client), Is.True);
            EventPipeline firstPipeline = client.Pipeline;

            Assert.That(Configure(client), Is.False);
            Assert.That(client.Pipeline, Is.SameAs(firstPipeline));
            Assert.That(logSink.Contains("Configure was called again"), Is.True);
        }

        /// <summary>Empty and secret keys are flagged.</summary>
        [Test]
        public void WarnsAboutSuspiciousKeys()
        {
            PipelineOptions emptyKeyOptions = TestKit.MakeOptions(storage.Path);
            emptyKeyOptions.IngestKey = "";
            TestKit.MakeClient(clock).Configure(emptyKeyOptions, transport, AutocaptureOptions.None);

            using var otherStorage = new TemporaryDirectory();
            PipelineOptions secretKeyOptions = TestKit.MakeOptions(otherStorage.Path);
            secretKeyOptions.IngestKey = "sdl_sec_abc";
            TestKit.MakeClient(clock).Configure(secretKeyOptions, transport, AutocaptureOptions.None);

            Assert.That(logSink.Contains("empty key"), Is.True);
            Assert.That(logSink.Contains("apps should ship a public sdl_pub_ key"), Is.True);
        }

        /// <summary>Events carry the device id, defaults under their own props, and a screen view carries its screen.</summary>
        [Test]
        public void StampsDeviceIdDefaultsAndScreens()
        {
            SimpleDataLogsClient client = TestKit.MakeClient(clock);
            client.SetDeviceId("device-7");
            client.SetDefaultProperties(new Dictionary<string, object> { ["app_version"] = "1.2.0", ["platform"] = "Android" });
            Configure(client);

            client.Track("level_complete", new Dictionary<string, object> { ["level"] = 3, ["platform"] = "override" });
            client.TrackScreen("MainMenu", new Dictionary<string, object> { ["screen"] = "ignored" });
            client.Flush(true);

            List<JsonElement> lines = TestKit.ParseLines(TestKit.ReadBody(transport.Requests.Single()));
            Assert.That(lines[0].GetProperty("event").GetString(), Is.EqualTo("level_complete"));
            Assert.That(lines[0].GetProperty("device_id").GetString(), Is.EqualTo("device-7"));
            Assert.That(lines[0].GetProperty("ts").GetString(), Is.EqualTo("2026-10-12T09:14:03.120Z"));
            Assert.That(lines[0].GetProperty("props").GetProperty("app_version").GetString(), Is.EqualTo("1.2.0"));
            Assert.That(lines[0].GetProperty("props").GetProperty("platform").GetString(), Is.EqualTo("override"));
            Assert.That(lines[0].GetProperty("props").GetProperty("level").GetInt32(), Is.EqualTo(3));
            Assert.That(lines[1].GetProperty("event").GetString(), Is.EqualTo("screen_view"));
            Assert.That(lines[1].GetProperty("props").GetProperty("screen").GetString(), Is.EqualTo("MainMenu"));
        }

        /// <summary>SetEnabled(false) before Configure deletes the leftover outbox and sends nothing.</summary>
        [Test]
        public void OptOutBeforeConfigureDeletesLeftovers()
        {
            SimpleDataLogsClient firstLaunch = TestKit.MakeClient(clock);
            Configure(firstLaunch);
            firstLaunch.Track("left_over", null);

            SimpleDataLogsClient secondLaunch = TestKit.MakeClient(clock);
            secondLaunch.SetEnabled(false);
            Configure(secondLaunch);
            secondLaunch.Track("ignored", null);
            secondLaunch.Flush(true);

            Assert.That(transport.Requests, Is.Empty);
            Assert.That(secondLaunch.Pipeline.PendingEventCount, Is.EqualTo(0));
            Assert.That(Directory.GetFiles(Path.Combine(storage.Path, EventOutbox.BatchesFolderName)), Is.Empty);
        }

        /// <summary>App launches report is_first_launch true once, then false.</summary>
        [Test]
        public void AppLaunchesMarkTheFirstLaunch()
        {
            SimpleDataLogsClient firstLaunch = TestKit.MakeClient(clock);
            firstLaunch.Configure(TestKit.MakeOptions(storage.Path), transport, AutocaptureOptions.AppLaunches);
            firstLaunch.Flush(true);

            SimpleDataLogsClient secondLaunch = TestKit.MakeClient(clock);
            secondLaunch.Configure(TestKit.MakeOptions(storage.Path), transport, AutocaptureOptions.AppLaunches);
            secondLaunch.Flush(true);

            JsonElement firstEvent = TestKit.ParseLines(TestKit.ReadBody(transport.Requests[0])).Single();
            JsonElement secondEvent = TestKit.ParseLines(TestKit.ReadBody(transport.Requests[1])).Single();
            Assert.That(firstEvent.GetProperty("event").GetString(), Is.EqualTo("app_launched"));
            Assert.That(firstEvent.GetProperty("props").GetProperty("is_first_launch").GetBoolean(), Is.True);
            Assert.That(secondEvent.GetProperty("props").GetProperty("is_first_launch").GetBoolean(), Is.False);
        }

        /// <summary>Sessions start before app_launched, tag every event, resume across a quick relaunch, and end after 30 minutes.</summary>
        [Test]
        public void SessionsTagEventsAndSurviveRelaunches()
        {
            AutocaptureOptions autocapture = AutocaptureOptions.Sessions | AutocaptureOptions.AppLaunches;
            SimpleDataLogsClient firstLaunch = TestKit.MakeClient(clock);
            firstLaunch.Configure(TestKit.MakeOptions(storage.Path), transport, autocapture);
            firstLaunch.Track("tap", null);
            clock.Advance(TimeSpan.FromMinutes(2));
            firstLaunch.AppWentToBackground();
            firstLaunch.Flush(true);

            List<JsonElement> firstLines = TestKit.ParseLines(TestKit.ReadBody(transport.Requests[0]));
            Assert.That(firstLines.Select(line => line.GetProperty("event").GetString()), Is.EqualTo(new[] { "session_start", "app_launched", "tap" }));
            string firstSessionId = firstLines[0].GetProperty("session_id").GetString();
            Assert.That(firstLines.All(line => line.GetProperty("session_id").GetString() == firstSessionId), Is.True);

            // A relaunch 10 minutes later resumes the stored session.
            clock.Advance(TimeSpan.FromMinutes(10));
            SimpleDataLogsClient secondLaunch = TestKit.MakeClient(clock, "relaunch-");
            secondLaunch.Configure(TestKit.MakeOptions(storage.Path), transport, AutocaptureOptions.Sessions);
            Assert.That(secondLaunch.SessionId, Is.EqualTo(firstSessionId));

            // 31 minutes in the background ends it at the last activity, with its duration, and starts a new one.
            secondLaunch.AppWentToBackground();
            DateTime backgroundedAtUtc = clock.UtcNow;
            clock.Advance(TimeSpan.FromMinutes(31));
            secondLaunch.AppBecameActive();
            secondLaunch.Flush(true);

            List<JsonElement> secondLines = TestKit.ParseLines(TestKit.ReadBody(transport.Requests[1]));
            Assert.That(secondLines.Select(line => line.GetProperty("event").GetString()), Is.EqualTo(new[] { "session_end", "session_start" }));
            Assert.That(secondLines[0].GetProperty("session_id").GetString(), Is.EqualTo(firstSessionId));
            Assert.That(secondLines[0].GetProperty("ts").GetString(), Is.EqualTo(EventEncoder.FormatTimestamp(backgroundedAtUtc)));
            Assert.That(secondLines[0].GetProperty("props").GetProperty("duration_s").GetInt64(), Is.EqualTo(12 * 60));
            Assert.That(secondLines[1].GetProperty("session_id").GetString(), Is.Not.EqualTo(firstSessionId));
        }

        /// <summary>Logged errors become app_error, bounded and rate limited; SDK lines and disabled capture are skipped.</summary>
        [Test]
        public void ErrorsAreCapturedWithinLimits()
        {
            SimpleDataLogsClient client = TestKit.MakeClient(clock);
            client.Configure(TestKit.MakeOptions(storage.Path), transport, AutocaptureOptions.Errors);

            foreach (int attempt in Enumerable.Range(1, 8))
            {
                client.RecordAppError("NullReferenceException: boom", new string('s', 5000), "Exception");
            }

            client.RecordAppError(SdkLog.Tag + " Dropped event", "", "Error");
            client.Flush(true);

            List<JsonElement> lines = TestKit.ParseLines(TestKit.ReadBody(transport.Requests.Single()));
            Assert.That(lines.Count, Is.EqualTo(5));
            Assert.That(lines[0].GetProperty("event").GetString(), Is.EqualTo("app_error"));
            Assert.That(lines[0].GetProperty("props").GetProperty("message").GetString(), Is.EqualTo("NullReferenceException: boom"));
            Assert.That(lines[0].GetProperty("props").GetProperty("stack").GetString().Length, Is.EqualTo(4000));
            Assert.That(lines[0].GetProperty("props").GetProperty("log_type").GetString(), Is.EqualTo("Exception"));

            // Without the option nothing is captured.
            using var otherStorage = new TemporaryDirectory();
            SimpleDataLogsClient quietClient = TestKit.MakeClient(clock);
            quietClient.Configure(TestKit.MakeOptions(otherStorage.Path), transport, AutocaptureOptions.None);
            quietClient.RecordAppError("boom", "", "Error");
            Assert.That(quietClient.Pipeline.PendingEventCount, Is.EqualTo(0));
        }

        // Configures with production defaults over the test folder and no autocapture.
        private bool Configure(SimpleDataLogsClient client)
        {
            return client.Configure(TestKit.MakeOptions(storage.Path), transport, AutocaptureOptions.None);
        }
    }
}
