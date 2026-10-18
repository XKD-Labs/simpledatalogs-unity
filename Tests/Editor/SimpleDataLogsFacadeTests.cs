using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using NUnit.Framework;

using XKDLabs.SimpleDataLogs.Core;

namespace XKDLabs.SimpleDataLogs.Editor.Tests
{
    /// <summary>The static facade inside the Unity editor, with a fake transport and a temp folder.</summary>
    [TestFixture]
    public sealed class SimpleDataLogsFacadeTests
    {
        /// <summary>The outbox folder for each test.</summary>
        private string storageDirectory;

        /// <summary>Requests the facade sent.</summary>
        private RecordingTransport transport;

        /// <summary>Starts every test from a clean facade, with SDK logs kept out of the console.</summary>
        [SetUp]
        public void SetUp()
        {
            SimpleDataLogs.ResetState();
            SdkLog.Sink = new SilentLogSink();
            storageDirectory = Path.Combine(Path.GetTempPath(), "sdl-editor-tests-" + Guid.NewGuid().ToString("N"));
            transport = new RecordingTransport();
        }

        /// <summary>Resets the facade and deletes the folder.</summary>
        [TearDown]
        public void TearDown()
        {
            SimpleDataLogs.ResetState();
            SdkLog.Sink = null;

            if (Directory.Exists(storageDirectory))
            {
                Directory.Delete(storageDirectory, true);
            }
        }

        /// <summary>Calls before Configure never throw.</summary>
        [Test]
        public void CallsBeforeConfigureAreSafe()
        {
            Assert.DoesNotThrow(() =>
            {
                SimpleDataLogs.Track("early");
                SimpleDataLogs.TrackScreen("Menu");
                SimpleDataLogs.SetDeviceId(null);
                SimpleDataLogs.SetDefaultProperties(null);
                SimpleDataLogs.Flush();
            });
        }

        /// <summary>Tracked events are journaled, then sent with the defaults and device id on Flush.</summary>
        [Test]
        public void TracksAndFlushes()
        {
            SimpleDataLogs.SetDeviceId("device-1");
            SimpleDataLogs.SetDefaultProperties(new Dictionary<string, object> { ["app_version"] = "1.0.0" });
            SimpleDataLogs.ConfigureForTests("sdl_pub_test", storageDirectory, transport, AutocaptureOptions.None);

            SimpleDataLogs.Track("level_complete", new Dictionary<string, object> { ["level"] = 3, ["duration_s"] = 41.5f });
            Assert.That(File.Exists(Path.Combine(storageDirectory, "journal.ndjson")), Is.True);

            SimpleDataLogs.Flush();

            Assert.That(transport.Bodies.Count, Is.EqualTo(1));
            Assert.That(transport.Bodies[0], Does.Contain("\"event\":\"level_complete\""));
            Assert.That(transport.Bodies[0], Does.Contain("\"device_id\":\"device-1\""));
            Assert.That(transport.Bodies[0], Does.Contain("\"duration_s\":41.5"));
            Assert.That(transport.Bodies[0], Does.Contain("\"app_version\":\"1.0.0\""));
        }

        /// <summary>The opt-out before Configure means nothing is journaled or sent.</summary>
        [Test]
        public void OptOutBeforeConfigureSendsNothing()
        {
            SimpleDataLogs.SetEnabled(false);
            SimpleDataLogs.ConfigureForTests("sdl_pub_test", storageDirectory, transport, AutocaptureOptions.AppLaunches);

            SimpleDataLogs.Track("ignored");
            SimpleDataLogs.Flush();

            Assert.That(transport.Bodies, Is.Empty);
            Assert.That(SimpleDataLogs.ClientForTests.Pipeline.PendingEventCount, Is.EqualTo(0));
        }

        /// <summary>Background moments flush, and errors autocapture skips the SDK's own lines.</summary>
        [Test]
        public void BackgroundFlushesAndErrorsAreCaptured()
        {
            SimpleDataLogs.ConfigureForTests("sdl_pub_test", storageDirectory, transport, AutocaptureOptions.Sessions | AutocaptureOptions.Errors);
            SimpleDataLogsClient client = SimpleDataLogs.ClientForTests;

            client.RecordAppError("[simpledatalogs] internal line", "", "Error");
            client.RecordAppError("NullReferenceException: boom", "at Player.Jump()", "Exception");
            SimpleDataLogs.HandleApplicationPause(true);

            Assert.That(transport.Bodies.Count, Is.EqualTo(1));
            Assert.That(transport.Bodies[0], Does.Contain("\"event\":\"session_start\""));
            Assert.That(transport.Bodies[0], Does.Contain("\"event\":\"app_error\""));
            Assert.That(transport.Bodies[0], Does.Not.Contain("internal line"));
        }

        /// <summary>A second Configure keeps the first.</summary>
        [Test]
        public void SecondConfigureIsIgnored()
        {
            SimpleDataLogs.ConfigureForTests("sdl_pub_first", storageDirectory, transport, AutocaptureOptions.None);
            EventPipeline firstPipeline = SimpleDataLogs.ClientForTests.Pipeline;

            SimpleDataLogs.ConfigureForTests("sdl_pub_second", storageDirectory, transport, AutocaptureOptions.None);

            Assert.That(SimpleDataLogs.ClientForTests.Pipeline, Is.SameAs(firstPipeline));
        }

        /// <summary>Answers every request with 202 and keeps the bodies.</summary>
        private sealed class RecordingTransport : IIngestTransport
        {
            /// <summary>Every body sent, as text.</summary>
            public List<string> Bodies { get; } = new List<string>();

            /// <summary>Records the body and answers 202 at once.</summary>
            public void Send(IngestRequest request, Action<IngestResponse> onComplete)
            {
                Bodies.Add(Encoding.UTF8.GetString(request.Body));
                onComplete(IngestResponse.FromStatus(202, null));
            }
        }

        /// <summary>Drops SDK log lines, so expected errors do not fail the test run.</summary>
        private sealed class SilentLogSink : ISdkLogSink
        {
            /// <summary>Ignores the line.</summary>
            public void Write(SdkLogLevel level, string message)
            {
            }
        }
    }
}
