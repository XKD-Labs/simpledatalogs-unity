using System;
using System.IO;
using System.Linq;
using System.Text;

using NUnit.Framework;

using XKDLabs.SimpleDataLogs.Core;

namespace XKDLabs.SimpleDataLogs.Tests
{
    /// <summary>The disk outbox: journal, seal, caps, splits and launch recovery.</summary>
    [TestFixture]
    public sealed class EventOutboxTests
    {
        /// <summary>A fixed time for file names.</summary>
        private static readonly DateTime NowUtc = new DateTime(2026, 10, 12, 9, 14, 3, DateTimeKind.Utc);

        /// <summary>The outbox folder for each test.</summary>
        private TemporaryDirectory storage;

        /// <summary>Records the SDK's log lines for each test.</summary>
        private RecordingLogSink logSink;

        /// <summary>Makes a fresh folder and a recording sink.</summary>
        [SetUp]
        public void SetUp()
        {
            storage = new TemporaryDirectory();
            logSink = new RecordingLogSink();
            SdkLog.Sink = logSink;
        }

        /// <summary>Deletes the folder and silences the SDK.</summary>
        [TearDown]
        public void TearDown()
        {
            storage.Dispose();
            SdkLog.Sink = null;
        }

        /// <summary>Appending writes whole lines; sealing renames the journal into a sortable batch file.</summary>
        [Test]
        public void SealRenamesTheJournalIntoABatch()
        {
            EventOutbox outbox = MakeRecoveredOutbox(new OutboxLimits());

            outbox.AppendToJournal(Line("{\"event\":\"a\"}"));
            outbox.AppendToJournal(Line("{\"event\":\"b\"}"));
            outbox.SealJournal(NowUtc);

            Assert.That(File.Exists(outbox.JournalPath), Is.False);
            Assert.That(outbox.SealedBatches.Count, Is.EqualTo(1));
            Assert.That(outbox.SealedBatches[0].EventCount, Is.EqualTo(2));
            Assert.That(outbox.SealedBatches[0].FileName, Does.Match("^batch-[0-9]{13}-[0-9]{6}\\.ndjson$"));
            Assert.That(File.ReadAllText(outbox.SealedBatches[0].FilePath), Is.EqualTo("{\"event\":\"a\"}\n{\"event\":\"b\"}\n"));
        }

        /// <summary>The journal overflows at the event-count limit or when the next line would pass the byte limit.</summary>
        [Test]
        public void ReportsOverflowAtEitherLimit()
        {
            EventOutbox outbox = MakeRecoveredOutbox(new OutboxLimits { MaxBatchEventCount = 2, MaxBatchBytes = 30 });

            Assert.That(outbox.WouldJournalOverflow(100), Is.False, "An empty journal always takes the line");

            outbox.AppendToJournal(Line("{\"event\":\"a\"}"));
            Assert.That(outbox.WouldJournalOverflow(14), Is.False);
            Assert.That(outbox.WouldJournalOverflow(17), Is.True);

            outbox.AppendToJournal(Line("{\"a\":1}"));
            Assert.That(outbox.WouldJournalOverflow(1), Is.True);
        }

        /// <summary>Over the event cap, the oldest batches are dropped with one warning.</summary>
        [Test]
        public void DropsTheOldestBatchesOverTheEventCap()
        {
            EventOutbox outbox = MakeRecoveredOutbox(new OutboxLimits { MaxTotalEventCount = 4 });

            string[] firstEventPerBatch = { "first", "second", "third" };
            foreach (string eventName in firstEventPerBatch)
            {
                outbox.AppendToJournal(Line("{\"event\":\"" + eventName + "\"}"));
                outbox.AppendToJournal(Line("{\"event\":\"" + eventName + "-b\"}"));
                outbox.SealJournal(NowUtc);
            }

            Assert.That(outbox.TotalEventCount, Is.EqualTo(4));
            Assert.That(File.ReadAllText(outbox.OldestSealedBatch.FilePath), Does.StartWith("{\"event\":\"second\"}"));
            Assert.That(logSink.Count("dropped the oldest 2"), Is.EqualTo(1));
        }

        /// <summary>The byte cap and the batch-file cap also drop the oldest batches.</summary>
        [Test]
        public void DropsTheOldestBatchesOverTheByteAndFileCaps()
        {
            EventOutbox byteCappedOutbox = MakeRecoveredOutbox(new OutboxLimits { MaxTotalBytes = 20 });
            for (int batchIndex = 0; batchIndex < 3; batchIndex++)
            {
                byteCappedOutbox.AppendToJournal(Line("{\"n\":" + batchIndex + "}"));
                byteCappedOutbox.SealJournal(NowUtc);
            }

            Assert.That(byteCappedOutbox.SealedBatches.Count, Is.EqualTo(2));

            using var otherStorage = new TemporaryDirectory();
            var fileCappedOutbox = new EventOutbox(otherStorage.Path, new OutboxLimits { MaxSealedBatchCount = 2 });
            fileCappedOutbox.RecoverAfterLaunch(NowUtc);
            for (int batchIndex = 0; batchIndex < 5; batchIndex++)
            {
                fileCappedOutbox.AppendToJournal(Line("{\"n\":" + batchIndex + "}"));
                fileCappedOutbox.SealJournal(NowUtc);
            }

            Assert.That(fileCappedOutbox.SealedBatches.Count, Is.EqualTo(2));
            Assert.That(File.ReadAllText(fileCappedOutbox.OldestSealedBatch.FilePath), Is.EqualTo("{\"n\":3}\n"));
        }

        /// <summary>A 413 split writes two halves that sort where the original did, then deletes the original.</summary>
        [Test]
        public void SplitsABatchInHalfInPlace()
        {
            EventOutbox outbox = MakeRecoveredOutbox(new OutboxLimits());
            foreach (int eventNumber in Enumerable.Range(1, 5))
            {
                outbox.AppendToJournal(Line("{\"n\":" + eventNumber + "}"));
            }

            outbox.SealJournal(NowUtc);
            outbox.AppendToJournal(Line("{\"n\":99}"));
            outbox.SealJournal(NowUtc);

            SealedBatch original = outbox.OldestSealedBatch;
            BatchSplitOutcome splitOutcome = outbox.SplitInHalf(original, File.ReadAllBytes(original.FilePath));

            Assert.That(splitOutcome, Is.EqualTo(BatchSplitOutcome.Split));
            Assert.That(File.Exists(original.FilePath), Is.False);
            Assert.That(outbox.SealedBatches.Select(batch => batch.EventCount), Is.EqualTo(new[] { 2, 3, 1 }));
            Assert.That(File.ReadAllText(outbox.SealedBatches[0].FilePath), Is.EqualTo("{\"n\":1}\n{\"n\":2}\n"));
            Assert.That(outbox.SealedBatches[0].FileName, Does.EndWith("-1.ndjson"));

            // The halves still sort before the newer batch after a relaunch.
            EventOutbox relaunchedOutbox = MakeRecoveredOutbox(new OutboxLimits());
            Assert.That(relaunchedOutbox.SealedBatches.Select(batch => batch.EventCount), Is.EqualTo(new[] { 2, 3, 1 }));
        }

        /// <summary>A single event that is still too large is dropped.</summary>
        [Test]
        public void DropsALoneEventOnSplit()
        {
            EventOutbox outbox = MakeRecoveredOutbox(new OutboxLimits());
            outbox.AppendToJournal(Line("{\"n\":1}"));
            outbox.SealJournal(NowUtc);

            SealedBatch loneBatch = outbox.OldestSealedBatch;
            BatchSplitOutcome splitOutcome = outbox.SplitInHalf(loneBatch, File.ReadAllBytes(loneBatch.FilePath));

            Assert.That(splitOutcome, Is.EqualTo(BatchSplitOutcome.DroppedLoneEvent));
            Assert.That(outbox.SealedBatches, Is.Empty);
            Assert.That(Directory.GetFiles(outbox.BatchesDirectory), Is.Empty);
        }

        /// <summary>A journal cut short mid-line by a crash is sealed without the torn line or junk lines.</summary>
        [Test]
        public void RecoversATruncatedJournal()
        {
            string journalPath = Path.Combine(storage.Path, EventOutbox.JournalFileName);
            File.WriteAllText(journalPath, "{\"n\":1}\n{\"n\":2}\nnot json\n{\"n\":3,\"te");

            EventOutbox outbox = MakeRecoveredOutbox(new OutboxLimits());

            Assert.That(File.Exists(journalPath), Is.False);
            Assert.That(outbox.SealedBatches.Count, Is.EqualTo(1));
            Assert.That(File.ReadAllText(outbox.OldestSealedBatch.FilePath), Is.EqualTo("{\"n\":1}\n{\"n\":2}\n"));
            Assert.That(logSink.Contains("Discarded 2 unreadable journal lines"), Is.True);
        }

        /// <summary>A big leftover journal is re-chunked into batches under today's limits.</summary>
        [Test]
        public void RechunksALeftoverJournal()
        {
            string journalPath = Path.Combine(storage.Path, EventOutbox.JournalFileName);
            File.WriteAllText(journalPath, string.Concat(Enumerable.Range(1, 7).Select(eventNumber => "{\"n\":" + eventNumber + "}\n")));

            EventOutbox outbox = MakeRecoveredOutbox(new OutboxLimits { MaxBatchEventCount = 3 });

            Assert.That(outbox.SealedBatches.Select(batch => batch.EventCount), Is.EqualTo(new[] { 3, 3, 1 }));
        }

        /// <summary>Leftover temporary files and set-aside batches are never queued.</summary>
        [Test]
        public void IgnoresTemporaryAndSetAsideFiles()
        {
            string batchesDirectory = Path.Combine(storage.Path, EventOutbox.BatchesFolderName);
            Directory.CreateDirectory(batchesDirectory);
            File.WriteAllText(Path.Combine(batchesDirectory, "batch-0000000000001-000001.ndjson.tmp"), "{\"n\":1}\n");
            File.WriteAllText(Path.Combine(batchesDirectory, "batch-0000000000002-000001.ndjson.unreadable"), "{\"n\":2}\n");
            File.WriteAllText(Path.Combine(batchesDirectory, "batch-0000000000003-000001.ndjson.unsupported"), "{\"n\":3}\n");
            File.WriteAllText(Path.Combine(batchesDirectory, "batch-0000000000004-000001.ndjson"), "{\"n\":4}\n");

            EventOutbox outbox = MakeRecoveredOutbox(new OutboxLimits());

            Assert.That(outbox.SealedBatches.Count, Is.EqualTo(1));
            Assert.That(File.Exists(Path.Combine(batchesDirectory, "batch-0000000000001-000001.ndjson.tmp")), Is.False);
            Assert.That(File.Exists(Path.Combine(batchesDirectory, "batch-0000000000002-000001.ndjson.unreadable")), Is.True);
        }

        /// <summary>Deleting everything removes journals and batches but keeps autocapture state.</summary>
        [Test]
        public void DeleteEverythingKeepsAutocaptureState()
        {
            EventOutbox outbox = MakeRecoveredOutbox(new OutboxLimits());
            outbox.AppendToJournal(Line("{\"n\":1}"));
            outbox.SealJournal(NowUtc);
            outbox.AppendToJournal(Line("{\"n\":2}"));
            File.WriteAllText(Path.Combine(storage.Path, SessionStore.FileName), "{}");

            outbox.DeleteEverything();

            Assert.That(outbox.TotalEventCount, Is.EqualTo(0));
            Assert.That(File.Exists(outbox.JournalPath), Is.False);
            Assert.That(Directory.GetFiles(outbox.BatchesDirectory), Is.Empty);
            Assert.That(File.Exists(Path.Combine(storage.Path, SessionStore.FileName)), Is.True);
        }

        /// <summary>A journal deleted behind the SDK's back is reported, and the counters restart.</summary>
        [Test]
        public void SurvivesAJournalDeletedBehindItsBack()
        {
            EventOutbox outbox = MakeRecoveredOutbox(new OutboxLimits());
            outbox.AppendToJournal(Line("{\"n\":1}"));
            File.Delete(outbox.JournalPath);

            outbox.SealJournal(NowUtc);

            Assert.That(outbox.TotalEventCount, Is.EqualTo(0));
            Assert.That(logSink.Contains("The journal disappeared"), Is.True);
        }

        // Makes an outbox over the test folder and runs launch recovery.
        private EventOutbox MakeRecoveredOutbox(OutboxLimits limits)
        {
            var outbox = new EventOutbox(storage.Path, limits);
            outbox.RecoverAfterLaunch(NowUtc);

            return outbox;
        }

        // Encodes one line with its newline.
        private static byte[] Line(string json)
        {
            return Encoding.UTF8.GetBytes(json + "\n");
        }
    }
}
