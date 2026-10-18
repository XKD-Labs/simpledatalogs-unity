using System;
using System.Collections.Generic;
using System.Text.Json;

using NUnit.Framework;

using XKDLabs.SimpleDataLogs.Core;

namespace XKDLabs.SimpleDataLogs.Tests
{
    /// <summary>The wire format: one valid JSON object per line, with the ingest field names.</summary>
    [TestFixture]
    public sealed class EventEncoderTests
    {
        /// <summary>Records the SDK's log lines for each test.</summary>
        private RecordingLogSink logSink;

        /// <summary>Swaps in a recording sink.</summary>
        [SetUp]
        public void SetUp()
        {
            logSink = new RecordingLogSink();
            SdkLog.Sink = logSink;
        }

        /// <summary>Silences the SDK again.</summary>
        [TearDown]
        public void TearDown()
        {
            SdkLog.Sink = null;
        }

        /// <summary>The fields come in a fixed order, and ts has millisecond precision with a Z.</summary>
        [Test]
        public void EncodesTheFixedFieldsInOrder()
        {
            var occurredAtUtc = new DateTime(2026, 10, 12, 9, 14, 3, 120, DateTimeKind.Utc);
            var properties = new Dictionary<string, object> { ["level"] = 3L };
            var trackedEvent = new TrackedEvent("level_complete", "8a3e5f0c-1111-4222-8333-944455556666", occurredAtUtc, "device-1", "session-1", properties);

            string encodedLine = EventEncoder.EncodeLine(trackedEvent);

            Assert.That(encodedLine, Is.EqualTo(
                "{\"event\":\"level_complete\",\"event_id\":\"8a3e5f0c-1111-4222-8333-944455556666\",\"ts\":\"2026-10-12T09:14:03.120Z\","
                + "\"device_id\":\"device-1\",\"session_id\":\"session-1\",\"props\":{\"level\":3}}"));
        }

        /// <summary>device_id and session_id are left out when unset, and props is always present.</summary>
        [Test]
        public void LeavesOutUnsetIdsButAlwaysWritesProps()
        {
            var trackedEvent = new TrackedEvent("app_launched", "id", DateTime.UnixEpoch, null, null, new Dictionary<string, object>());

            string encodedLine = EventEncoder.EncodeLine(trackedEvent);

            Assert.That(encodedLine, Is.EqualTo("{\"event\":\"app_launched\",\"event_id\":\"id\",\"ts\":\"1970-01-01T00:00:00.000Z\",\"props\":{}}"));
        }

        /// <summary>A local time is converted to UTC before formatting.</summary>
        [Test]
        public void FormatsLocalTimesInUtc()
        {
            var momentUtc = new DateTime(2026, 1, 2, 3, 4, 5, 6, DateTimeKind.Utc);

            Assert.That(EventEncoder.FormatTimestamp(momentUtc.ToLocalTime()), Is.EqualTo("2026-01-02T03:04:05.006Z"));
        }

        /// <summary>Quotes, backslashes, newlines and control characters are escaped, so one event is one line.</summary>
        [Test]
        public void EscapesEverythingThatCouldBreakTheLine()
        {
            string trickyText = "quote\" backslash\\ newline\n return\r tab\t bell\u0007 nul\u0000 separator\u2028 emoji\U0001F600";
            var properties = new Dictionary<string, object> { ["text"] = trickyText };
            var trackedEvent = new TrackedEvent("note", "id", DateTime.UnixEpoch, null, null, properties);

            string encodedLine = EventEncoder.EncodeLine(trackedEvent);

            Assert.That(encodedLine, Does.Not.Contain("\n"));
            Assert.That(encodedLine, Does.Not.Contain("\r"));
            Assert.That(encodedLine, Does.Contain("\\u0007"));
            Assert.That(encodedLine, Does.Contain("\\u0000"));
            Assert.That(encodedLine, Does.Contain("\\u2028"));

            // It parses back to exactly the original text.
            using JsonDocument parsedLine = JsonDocument.Parse(encodedLine);
            Assert.That(parsedLine.RootElement.GetProperty("props").GetProperty("text").GetString(), Is.EqualTo(trickyText));
        }

        /// <summary>A lone surrogate becomes U+FFFD, so the line is still valid UTF-8.</summary>
        [Test]
        public void ReplacesLoneSurrogates()
        {
            var properties = new Dictionary<string, object> { ["text"] = "a\uD800b" };
            var trackedEvent = new TrackedEvent("note", "id", DateTime.UnixEpoch, null, null, properties);

            using JsonDocument parsedLine = JsonDocument.Parse(EventEncoder.EncodeLine(trackedEvent));

            Assert.That(parsedLine.RootElement.GetProperty("props").GetProperty("text").GetString(), Is.EqualTo("a\uFFFDb"));
        }

        /// <summary>Strings, integers, floats and booleans keep their JSON kinds; floats keep the digits written.</summary>
        [Test]
        public void NormalizesTheFourSupportedKinds()
        {
            var rawProperties = new Dictionary<string, object>
            {
                ["sku"] = "gem_pack",
                ["level"] = 3,
                ["byte_value"] = (byte)7,
                ["big"] = ulong.MaxValue,
                ["price"] = 0.99f,
                ["ratio"] = 0.5,
                ["is_pro"] = true,
                ["money"] = 1.25m,
            };

            Dictionary<string, object> normalized = PropertyNormalizer.Normalize(rawProperties, "purchase");
            var trackedEvent = new TrackedEvent("purchase", "id", DateTime.UnixEpoch, null, null, normalized);
            string encodedLine = EventEncoder.EncodeLine(trackedEvent);

            Assert.That(encodedLine, Does.Contain("\"price\":0.99"));
            Assert.That(encodedLine, Does.Contain("\"big\":18446744073709551615"));
            Assert.That(encodedLine, Does.Contain("\"byte_value\":7"));
            Assert.That(encodedLine, Does.Contain("\"is_pro\":true"));
            Assert.That(encodedLine, Does.Contain("\"level\":3"));
            Assert.That(encodedLine, Does.Contain("\"money\":1.25"));
            Assert.That(encodedLine, Does.Contain("\"ratio\":0.5"));
            Assert.That(encodedLine, Does.Contain("\"sku\":\"gem_pack\""));
        }

        /// <summary>NaN and infinity are dropped with a log line; the rest of the event survives.</summary>
        [Test]
        public void DropsNaNAndInfinityWithALogLine()
        {
            var rawProperties = new Dictionary<string, object>
            {
                ["nan"] = double.NaN,
                ["infinite"] = float.PositiveInfinity,
                ["kept"] = 1,
            };

            Dictionary<string, object> normalized = PropertyNormalizer.Normalize(rawProperties, "score");

            Assert.That(normalized.Keys, Is.EquivalentTo(new[] { "kept" }));
            Assert.That(logSink.Contains("Dropped property nan"), Is.True);
            Assert.That(logSink.Contains("Dropped property infinite"), Is.True);
        }

        /// <summary>Null, lists, maps and objects are dropped with a log line.</summary>
        [Test]
        public void DropsUnsupportedTypesWithALogLine()
        {
            var rawProperties = new Dictionary<string, object>
            {
                ["missing"] = null,
                ["list"] = new List<int> { 1 },
                ["map"] = new Dictionary<string, object>(),
                ["object"] = new object(),
                ["weekday"] = DayOfWeek.Monday,
                [""] = "empty name",
                ["kept"] = "yes",
            };

            Dictionary<string, object> normalized = PropertyNormalizer.Normalize(rawProperties, "mixed");

            Assert.That(normalized.Keys, Is.EquivalentTo(new[] { "kept" }));
            Assert.That(logSink.Count("Dropped"), Is.EqualTo(6));
        }

        /// <summary>The event's own properties win over defaults of the same name.</summary>
        [Test]
        public void MergesDefaultsUnderEventProperties()
        {
            var defaultProperties = new Dictionary<string, object> { ["app_version"] = "1.0", ["platform"] = "Android" };
            var eventProperties = new Dictionary<string, object> { ["platform"] = "IPhonePlayer", ["level"] = 2L };

            Dictionary<string, object> merged = PropertyNormalizer.Merge(defaultProperties, eventProperties);

            Assert.That(merged["app_version"], Is.EqualTo("1.0"));
            Assert.That(merged["platform"], Is.EqualTo("IPhonePlayer"));
            Assert.That(merged["level"], Is.EqualTo(2L));
        }

        /// <summary>Props keys are sorted, so the same props always encode the same way.</summary>
        [Test]
        public void SortsPropertyKeys()
        {
            var properties = new Dictionary<string, object> { ["b"] = 1L, ["a"] = 2L, ["C"] = 3L };
            var trackedEvent = new TrackedEvent("sorted", "id", DateTime.UnixEpoch, null, null, properties);

            Assert.That(EventEncoder.EncodeLine(trackedEvent), Does.EndWith("\"props\":{\"C\":3,\"a\":2,\"b\":1}}"));
        }
    }
}
