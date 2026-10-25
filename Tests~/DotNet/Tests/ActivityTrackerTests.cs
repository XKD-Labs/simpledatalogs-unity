using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using NUnit.Framework;

using XKDLabs.SimpleDataLogs.Core;

namespace XKDLabs.SimpleDataLogs.Tests
{
    /// <summary>Activity stamps: app_state and engaged_ms on events tracked now, from pause and focus.</summary>
    [TestFixture]
    public sealed class ActivityTrackerTests
    {
        /// <summary>A fixed start time.</summary>
        private static readonly DateTime StartUtc = new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>Foreground time counts between stamps, and background time is left out.</summary>
        [Test]
        public void CountsForegroundTimeAndLeavesBackgroundTimeOut()
        {
            var tracker = new ActivityTracker(true, StartUtc);

            Assert.That(tracker.Stamp(StartUtc.AddSeconds(30)), Is.EqualTo(new Dictionary<string, object> { ["app_state"] = "active", ["engaged_ms"] = 30_000L }));

            tracker.SetActive(false, StartUtc.AddSeconds(40));
            Assert.That(tracker.Stamp(StartUtc.AddMinutes(10)), Is.EqualTo(new Dictionary<string, object> { ["app_state"] = "background" }));

            // Back in front for 20 seconds after an hour away; a repeated notification changes nothing.
            tracker.SetActive(true, StartUtc.AddHours(1));
            tracker.SetActive(true, StartUtc.AddHours(1).AddSeconds(10));
            Assert.That(tracker.Stamp(StartUtc.AddHours(1).AddSeconds(20)), Is.EqualTo(new Dictionary<string, object> { ["app_state"] = "active", ["engaged_ms"] = 20_000L }));
        }

        /// <summary>A finished stretch counts once, and an engaged time never passes a day.</summary>
        [Test]
        public void CountsAFinishedStretchOnceAndCapsAtADay()
        {
            var tracker = new ActivityTracker(false, StartUtc);

            tracker.SetActive(true, StartUtc.AddMilliseconds(100));
            tracker.SetActive(false, StartUtc.AddMilliseconds(145));
            tracker.SetActive(true, StartUtc.AddMilliseconds(200));
            Assert.That(tracker.Stamp(StartUtc.AddMilliseconds(210))["engaged_ms"], Is.EqualTo(55L));
            Assert.That(tracker.Stamp(StartUtc.AddDays(2))["engaged_ms"], Is.EqualTo(86_400_000L));
        }

        /// <summary>Events tracked after the app state is known carry the stamp; the caller's own value wins.</summary>
        [Test]
        public void StampsTrackedEventsOnceTheStateIsKnown()
        {
            using var storage = new TemporaryDirectory();
            var clock = new FakeClock();
            var transport = new FakeTransport();
            SimpleDataLogsClient client = TestKit.MakeClient(clock);
            client.Configure(TestKit.MakeOptions(storage.Path), transport, AutocaptureOptions.None);

            client.Track("before_lifecycle", null);
            client.SetAppActive(true);
            clock.Advance(TimeSpan.FromSeconds(5));
            client.Track("opened", null);
            client.SetAppActive(false);
            clock.Advance(TimeSpan.FromMinutes(1));
            client.Track("synced", null);
            client.Track("marked", new Dictionary<string, object> { ["app_state"] = "active" });
            client.Flush(true);

            List<JsonElement> lines = transport.Requests.SelectMany(request => TestKit.ParseLines(TestKit.ReadBody(request))).ToList();
            Assert.That(lines[0].TryGetProperty("props", out JsonElement firstProps) && firstProps.TryGetProperty("app_state", out _), Is.False);
            Assert.That(lines[1].GetProperty("props").GetProperty("app_state").GetString(), Is.EqualTo("active"));
            Assert.That(lines[1].GetProperty("props").GetProperty("engaged_ms").GetInt64(), Is.EqualTo(5_000));
            Assert.That(lines[2].GetProperty("props").GetProperty("app_state").GetString(), Is.EqualTo("background"));
            Assert.That(lines[2].GetProperty("props").TryGetProperty("engaged_ms", out _), Is.False);
            Assert.That(lines[3].GetProperty("props").GetProperty("app_state").GetString(), Is.EqualTo("active"));
        }
    }
}
