using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using NUnit.Framework;

using XKDLabs.SimpleDataLogs.Core;

namespace XKDLabs.SimpleDataLogs.Tests
{
    /// <summary>Core checks that matter on players: run them on IL2CPP and WebGL builds to catch stripping or AOT gaps.</summary>
    [TestFixture]
    public sealed class CorePlayerTests
    {
        /// <summary>The encoder produces the exact wire line under the player's runtime.</summary>
        [Test]
        public void EncoderMatchesTheWireFormat()
        {
            var occurredAtUtc = new DateTime(2026, 10, 12, 9, 14, 3, 120, DateTimeKind.Utc);
            Dictionary<string, object> properties = PropertyNormalizer.Normalize(new Dictionary<string, object> { ["level"] = 3, ["price"] = 0.99f, ["note"] = "a\nb" }, "test");
            var trackedEvent = new TrackedEvent("purchase", "id-1", occurredAtUtc, null, null, properties);

            string encodedLine = EventEncoder.EncodeLine(trackedEvent);

            Assert.That(encodedLine, Is.EqualTo("{\"event\":\"purchase\",\"event_id\":\"id-1\",\"ts\":\"2026-10-12T09:14:03.120Z\",\"props\":{\"level\":3,\"note\":\"a\\nb\",\"price\":0.99}}"));
        }

        /// <summary>Gzip either works or reports itself unavailable; it never throws.</summary>
        [Test]
        public void GzipNeverThrows()
        {
            byte[] body = Encoding.UTF8.GetBytes(new string('x', 4096));

            Assert.DoesNotThrow(() => BodyCompressor.TryGzip(body));
        }

        /// <summary>The outbox journals, seals and recovers in the player's file system.</summary>
        [Test]
        public void OutboxRoundTripsOnThisPlatform()
        {
            string storageDirectory = Path.Combine(Path.GetTempPath(), "sdl-player-tests-" + Guid.NewGuid().ToString("N"));
            DateTime nowUtc = DateTime.UtcNow;

            try
            {
                var outbox = new EventOutbox(storageDirectory, new OutboxLimits());
                outbox.RecoverAfterLaunch(nowUtc);
                outbox.AppendToJournal(Encoding.UTF8.GetBytes("{\"n\":1}\n"));

                var relaunchedOutbox = new EventOutbox(storageDirectory, new OutboxLimits());
                relaunchedOutbox.RecoverAfterLaunch(nowUtc);

                Assert.That(relaunchedOutbox.TotalEventCount, Is.EqualTo(1));
            }
            finally
            {
                if (Directory.Exists(storageDirectory))
                {
                    Directory.Delete(storageDirectory, true);
                }
            }
        }
    }
}
