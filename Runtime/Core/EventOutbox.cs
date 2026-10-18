using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace XKDLabs.SimpleDataLogs.Core
{
    /// <summary>One sealed batch file: an NDJSON request body waiting for its 202.</summary>
    internal sealed class SealedBatch
    {
        /// <summary>Indexes one batch file.</summary>
        public SealedBatch(string filePath, int eventCount, int byteCount)
        {
            FilePath = filePath;
            EventCount = eventCount;
            ByteCount = byteCount;
        }

        /// <summary>Where the batch lives on disk.</summary>
        public string FilePath { get; }

        /// <summary>How many events (lines) the batch holds.</summary>
        public int EventCount { get; }

        /// <summary>The size of the batch body in bytes.</summary>
        public int ByteCount { get; }

        /// <summary>The file name, which sorts oldest first.</summary>
        public string FileName => Path.GetFileName(FilePath);
    }

    /// <summary>The size limits the outbox enforces.</summary>
    internal sealed class OutboxLimits
    {
        /// <summary>The most events one sealed batch may hold.</summary>
        public int MaxBatchEventCount { get; set; } = 50;

        /// <summary>The most bytes one sealed batch (one request body) may hold.</summary>
        public int MaxBatchBytes { get; set; } = 256000;

        /// <summary>The most events kept across the journal and every sealed batch.</summary>
        public int MaxTotalEventCount { get; set; } = 10000;

        /// <summary>The most bytes kept across the journal and every sealed batch.</summary>
        public int MaxTotalBytes { get; set; } = 10 * 1024 * 1024;

        /// <summary>The most sealed batch files kept.</summary>
        public int MaxSealedBatchCount { get; set; } = 500;
    }

    /// <summary>What happened to a batch the server called too large.</summary>
    internal enum BatchSplitOutcome
    {
        /// <summary>The batch was replaced by its two halves.</summary>
        Split,

        /// <summary>The batch held a single event, which was dropped because it can never be accepted.</summary>
        DroppedLoneEvent,

        /// <summary>The batch had already left the outbox, e.g. trimmed by the cap while in flight.</summary>
        AlreadyRemoved,
    }

    /// <summary>The crash-safe disk outbox: an append-only journal, plus sealed batch files deleted only after a 202.</summary>
    internal sealed class EventOutbox
    {
        /// <summary>The live journal's file name inside the outbox folder.</summary>
        public const string JournalFileName = "journal.ndjson";

        /// <summary>Every journal file, live or set aside after a failed recovery, starts with this.</summary>
        public const string JournalFilePrefix = "journal";

        /// <summary>The folder of sealed batches inside the outbox folder.</summary>
        public const string BatchesFolderName = "batches";

        /// <summary>Every sealed batch file name starts with this.</summary>
        public const string BatchFilePrefix = "batch-";

        /// <summary>Journals and sealed batches both end with this extension.</summary>
        public const string BatchFileExtension = ".ndjson";

        /// <summary>The extra extension of a batch set aside after repeated read failures.</summary>
        public const string UnreadableFileExtension = ".unreadable";

        /// <summary>The extra extension of a batch ingest refused as unsupported (415); it is never resent.</summary>
        public const string UnsupportedFileExtension = ".unsupported";

        /// <summary>The extension of a half-written file; leftovers are deleted at launch.</summary>
        public const string TemporaryFileExtension = ".tmp";

        /// <summary>The NDJSON line separator.</summary>
        private const byte NewlineByte = (byte)'\n';

        /// <summary>The epoch batch names count from.</summary>
        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>Batch and total size limits.</summary>
        private readonly OutboxLimits limits;

        /// <summary>Every sealed batch on disk, oldest first.</summary>
        private readonly List<SealedBatch> sealedBatches = new List<SealedBatch>();

        /// <summary>A per-launch counter that keeps file names sorted within one millisecond.</summary>
        private int fileNameSequence;

        /// <summary>Points the outbox at its folder; nothing touches the disk until RecoverAfterLaunch.</summary>
        public EventOutbox(string directory, OutboxLimits limits)
        {
            Directory = directory;
            this.limits = limits;
            JournalPath = Path.Combine(directory, JournalFileName);
            BatchesDirectory = Path.Combine(directory, BatchesFolderName);
        }

        /// <summary>The outbox root folder.</summary>
        public string Directory { get; }

        /// <summary>The live journal every tracked event is appended to.</summary>
        public string JournalPath { get; }

        /// <summary>The folder holding sealed batches.</summary>
        public string BatchesDirectory { get; }

        /// <summary>Reads a whole file; tests swap it to simulate unreadable batches.</summary>
        internal Func<string, byte[]> ReadFileBytes { get; set; } = File.ReadAllBytes;

        /// <summary>How many events the journal holds right now.</summary>
        public int JournalEventCount { get; private set; }

        /// <summary>How many bytes the journal holds right now.</summary>
        public int JournalByteCount { get; private set; }

        /// <summary>Every sealed batch, oldest first.</summary>
        public IReadOnlyList<SealedBatch> SealedBatches => sealedBatches;

        /// <summary>Events held across the journal and every sealed batch.</summary>
        public int TotalEventCount
        {
            get
            {
                int heldEventCount = JournalEventCount;
                foreach (SealedBatch sealedBatch in sealedBatches)
                {
                    heldEventCount += sealedBatch.EventCount;
                }

                return heldEventCount;
            }
        }

        /// <summary>Bytes held across the journal and every sealed batch.</summary>
        public long TotalByteCount
        {
            get
            {
                long heldByteCount = JournalByteCount;
                foreach (SealedBatch sealedBatch in sealedBatches)
                {
                    heldByteCount += sealedBatch.ByteCount;
                }

                return heldByteCount;
            }
        }

        /// <summary>The batch to send next, or null.</summary>
        public SealedBatch OldestSealedBatch => sealedBatches.Count > 0 ? sealedBatches[0] : null;

        /// <summary>The cheapest batch to send as a 401 re-probe, or null.</summary>
        public SealedBatch SmallestSealedBatch
        {
            get
            {
                SealedBatch smallestBatch = null;
                foreach (SealedBatch sealedBatch in sealedBatches)
                {
                    if (smallestBatch == null || sealedBatch.ByteCount < smallestBatch.ByteCount)
                    {
                        smallestBatch = sealedBatch;
                    }
                }

                return smallestBatch;
            }
        }

        /// <summary>Creates the folders, seals every journal earlier launches left, and indexes the sealed batches.</summary>
        public void RecoverAfterLaunch(DateTime nowUtc)
        {
            // A folder failure is reported at the end, after everything already on disk is recovered.
            Exception folderCreationError = null;
            try
            {
                CreateFoldersIfNeeded();
            }
            catch (Exception creationError)
            {
                folderCreationError = creationError;
            }

            // Half-written files from a crash mid-write are never complete batches.
            DeleteTemporaryFiles();

            // Seal whatever earlier launches journaled but never sealed, including after a crash.
            SealLeftoverJournals(nowUtc);

            // Index every sealed batch, oldest first.
            sealedBatches.Clear();
            sealedBatches.AddRange(LoadSealedBatches());
            JournalEventCount = 0;
            JournalByteCount = 0;

            // A long offline period may have pushed the outbox over its cap.
            EnforceCap();

            if (folderCreationError != null)
            {
                throw folderCreationError;
            }
        }

        /// <summary>True when the journal must be sealed before this line: it holds a full batch, or the line would overfill a request.</summary>
        public bool WouldJournalOverflow(int lineByteCountWithNewline)
        {
            if (JournalEventCount == 0)
            {
                return false;
            }

            bool isJournalFull = JournalEventCount >= limits.MaxBatchEventCount;
            bool wouldExceedRequestBytes = JournalByteCount + lineByteCountWithNewline > limits.MaxBatchBytes;

            return isJournalFull || wouldExceedRequestBytes;
        }

        /// <summary>Appends one encoded line, newline included, so a crash from here on loses nothing.</summary>
        public void AppendToJournal(byte[] lineWithNewline)
        {
            // The folders may have been removed behind the SDK's back.
            if (!File.Exists(JournalPath))
            {
                CreateFoldersIfNeeded();
            }

            using (var journalStream = new FileStream(JournalPath, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                // Remember where this line starts, so a failed write can be cut back off.
                long lineStartOffset = journalStream.Position;

                try
                {
                    journalStream.Write(lineWithNewline, 0, lineWithNewline.Length);
                    journalStream.Flush();
                }
                catch (Exception)
                {
                    // A full disk can fail mid-write; a torn line must never fuse with the next one.
                    TryTruncate(journalStream, lineStartOffset);
                    throw;
                }
            }

            // Count the line only once it is fully on disk.
            JournalEventCount += 1;
            JournalByteCount += lineWithNewline.Length;
        }

        /// <summary>Turns the journal into a sealed batch with one atomic rename, then trims the outbox to its cap.</summary>
        public void SealJournal(DateTime nowUtc)
        {
            if (JournalEventCount == 0)
            {
                return;
            }

            // A journal removed behind the SDK's back leaves nothing to seal.
            if (!File.Exists(JournalPath))
            {
                SdkLog.Warning($"The journal disappeared before sealing; {JournalEventCount} events are gone");
                JournalEventCount = 0;
                JournalByteCount = 0;
                return;
            }

            // A rename is atomic, so the events are always in exactly one file.
            CreateFoldersIfNeeded();
            string batchPath = MakeBatchPath(nowUtc);
            File.Move(JournalPath, batchPath);

            // Index the new batch and start an empty journal.
            sealedBatches.Add(new SealedBatch(batchPath, JournalEventCount, JournalByteCount));
            JournalEventCount = 0;
            JournalByteCount = 0;

            EnforceCap();
        }

        /// <summary>Reads a batch body, or returns null (and forgets the batch) when its file vanished; throws when unreadable.</summary>
        public byte[] ReadBody(SealedBatch batch)
        {
            // A missing file can never be sent, so it leaves the index.
            if (!File.Exists(batch.FilePath))
            {
                sealedBatches.Remove(batch);
                return null;
            }

            return ReadFileBytes(batch.FilePath);
        }

        /// <summary>Deletes a delivered batch.</summary>
        public void Delete(SealedBatch batch)
        {
            sealedBatches.Remove(batch);
            TryDeleteFile(batch.FilePath);
        }

        /// <summary>Moves a batch out of the queue under an extra extension, keeping the file for inspection.</summary>
        public void SetAside(SealedBatch batch, string extraExtension)
        {
            sealedBatches.Remove(batch);

            // If even the rename fails, the next launch indexes the file again and retries it.
            try
            {
                File.Move(batch.FilePath, batch.FilePath + extraExtension);
            }
            catch (Exception moveError)
            {
                SdkLog.Warning($"Could not set aside {batch.FileName}: {moveError.Message}");
            }
        }

        /// <summary>Replaces a batch the server called too large with its two halves, in the same queue position.</summary>
        public BatchSplitOutcome SplitInHalf(SealedBatch batch, byte[] body)
        {
            // The cap may have trimmed the batch while it was in flight.
            int batchIndex = sealedBatches.IndexOf(batch);
            if (batchIndex < 0)
            {
                return BatchSplitOutcome.AlreadyRemoved;
            }

            // A lone event that is still too large can never be accepted.
            List<ArraySegment<byte>> eventLines = SplitLines(body);
            if (eventLines.Count <= 1)
            {
                Delete(batch);
                return BatchSplitOutcome.DroppedLoneEvent;
            }

            // Write both halves before removing the original, so a crash can only duplicate, never lose.
            int middleIndex = eventLines.Count / 2;
            SealedBatch firstHalf = WriteBatchFile(eventLines.GetRange(0, middleIndex), MakeHalfPath(batch, 1));
            SealedBatch secondHalf = WriteBatchFile(eventLines.GetRange(middleIndex, eventLines.Count - middleIndex), MakeHalfPath(batch, 2));

            // Swap the original for its halves.
            sealedBatches.RemoveAt(batchIndex);
            sealedBatches.InsertRange(batchIndex, new[] { firstHalf, secondHalf });
            TryDeleteFile(batch.FilePath);

            return BatchSplitOutcome.Split;
        }

        /// <summary>Deletes every journal and every batch file, for an opt-out; autocapture state stays.</summary>
        public void DeleteEverything()
        {
            // Remove the live journal and any set aside by an earlier failed recovery.
            foreach (string journalFilePath in ListLeftoverJournalPaths())
            {
                TryDeleteFile(journalFilePath);
            }

            // Remove every batch file, indexed, unindexed or set aside.
            if (System.IO.Directory.Exists(BatchesDirectory))
            {
                foreach (string batchFilePath in System.IO.Directory.GetFiles(BatchesDirectory))
                {
                    TryDeleteFile(batchFilePath);
                }
            }

            // Reset the in-memory view to match the empty folders.
            sealedBatches.Clear();
            JournalEventCount = 0;
            JournalByteCount = 0;
        }

        /// <summary>Splits an NDJSON body into its non-empty lines, without newlines.</summary>
        public static List<ArraySegment<byte>> SplitLines(byte[] ndjsonBody)
        {
            var eventLines = new List<ArraySegment<byte>>();
            int lineStart = 0;

            for (int byteIndex = 0; byteIndex <= ndjsonBody.Length; byteIndex++)
            {
                bool isLineEnd = byteIndex == ndjsonBody.Length || ndjsonBody[byteIndex] == NewlineByte;
                if (!isLineEnd)
                {
                    continue;
                }

                if (byteIndex > lineStart)
                {
                    eventLines.Add(new ArraySegment<byte>(ndjsonBody, lineStart, byteIndex - lineStart));
                }

                lineStart = byteIndex + 1;
            }

            return eventLines;
        }

        // Creates the outbox and batch folders when missing.
        private void CreateFoldersIfNeeded()
        {
            System.IO.Directory.CreateDirectory(BatchesDirectory);
        }

        // Cuts a stream back to an offset, ignoring a failure; the caller is already handling one.
        private static void TryTruncate(FileStream stream, long offset)
        {
            try
            {
                stream.SetLength(offset);
            }
            catch (Exception)
            {
                // Recovery drops a torn final line anyway.
            }
        }

        // Deletes a file, ignoring a failure.
        private static void TryDeleteFile(string filePath)
        {
            try
            {
                File.Delete(filePath);
            }
            catch (Exception deleteError)
            {
                SdkLog.Warning($"Could not delete {Path.GetFileName(filePath)}: {deleteError.Message}");
            }
        }

        // Removes half-written files a crash left in the batch folder.
        private void DeleteTemporaryFiles()
        {
            if (!System.IO.Directory.Exists(BatchesDirectory))
            {
                return;
            }

            foreach (string temporaryFilePath in System.IO.Directory.GetFiles(BatchesDirectory, "*" + TemporaryFileExtension))
            {
                TryDeleteFile(temporaryFilePath);
            }
        }

        // Every journal file in the outbox folder, live or set aside.
        private List<string> ListLeftoverJournalPaths()
        {
            var journalPaths = new List<string>();
            if (!System.IO.Directory.Exists(Directory))
            {
                return journalPaths;
            }

            foreach (string filePath in System.IO.Directory.GetFiles(Directory, JournalFilePrefix + "*" + BatchFileExtension))
            {
                journalPaths.Add(filePath);
            }

            journalPaths.Sort(string.CompareOrdinal);
            return journalPaths;
        }

        // Seals every journal earlier launches left: the live one, plus any set aside by a failed recovery.
        private void SealLeftoverJournals(DateTime nowUtc)
        {
            foreach (string leftoverJournalPath in ListLeftoverJournalPaths())
            {
                try
                {
                    SealLeftoverJournal(leftoverJournalPath, nowUtc);
                }
                catch (Exception sealError)
                {
                    SdkLog.Error($"Could not seal a leftover journal ({sealError.Message}); keeping it for the next launch");

                    // The live journal's path must be free, so this launch's events never mix with old lines.
                    if (Path.GetFileName(leftoverJournalPath) == JournalFileName)
                    {
                        SetLiveJournalAside(nowUtc);
                    }
                }
            }
        }

        // Seals one leftover journal into batches, dropping a line torn by a crash mid-append, then removes it.
        private void SealLeftoverJournal(string leftoverJournalPath, DateTime nowUtc)
        {
            byte[] journalBytes = File.ReadAllBytes(leftoverJournalPath);

            // A final line with no newline was cut short mid-write.
            int completeByteCount = LastIndexOfNewline(journalBytes) + 1;
            int tornLineCount = completeByteCount < journalBytes.Length ? 1 : 0;

            // Keep only lines that look like whole JSON objects.
            var validEventLines = new List<ArraySegment<byte>>();
            foreach (ArraySegment<byte> journalLine in SplitLines(Slice(journalBytes, completeByteCount)))
            {
                if (LooksLikeJsonObject(journalLine))
                {
                    validEventLines.Add(journalLine);
                }
                else
                {
                    tornLineCount += 1;
                }
            }

            if (tornLineCount > 0)
            {
                SdkLog.Warning($"Discarded {tornLineCount} unreadable journal lines left by a previous launch");
            }

            // Re-chunk into batches that respect today's limits.
            foreach (List<ArraySegment<byte>> batchLines in ChunkIntoBatches(validEventLines))
            {
                WriteBatchFile(batchLines, MakeBatchPath(nowUtc));
            }

            // Only now that every event is in a sealed batch is the journal safe to remove.
            File.Delete(leftoverJournalPath);
        }

        // Renames the live journal to a unique set-aside name, so a fresh journal starts.
        private void SetLiveJournalAside(DateTime nowUtc)
        {
            string setAsidePath = Path.Combine(Directory, JournalFilePrefix + "-" + MakeSortableFileStamp(nowUtc) + BatchFileExtension);

            try
            {
                File.Move(JournalPath, setAsidePath);
            }
            catch (Exception)
            {
                SdkLog.Error("Could not set the leftover journal aside; its events may be sent twice");
            }
        }

        // Lists sealed batch files, oldest first, counting events and bytes in each.
        private List<SealedBatch> LoadSealedBatches()
        {
            var loadedBatches = new List<SealedBatch>();
            if (!System.IO.Directory.Exists(BatchesDirectory))
            {
                return loadedBatches;
            }

            // Set-aside files have another extension, so only live batches match.
            string[] batchFilePaths = System.IO.Directory.GetFiles(BatchesDirectory, BatchFilePrefix + "*" + BatchFileExtension);
            Array.Sort(batchFilePaths, (firstPath, secondPath) => string.CompareOrdinal(Path.GetFileName(firstPath), Path.GetFileName(secondPath)));

            foreach (string batchFilePath in batchFilePaths)
            {
                // GetFiles matches "*.ndjson" loosely on some platforms, so check the real extension.
                if (!batchFilePath.EndsWith(BatchFileExtension, StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    byte[] batchBody = ReadFileBytes(batchFilePath);
                    loadedBatches.Add(new SealedBatch(batchFilePath, SplitLines(batchBody).Count, batchBody.Length));
                }
                catch (Exception)
                {
                    // An unreadable file is skipped and retried next launch.
                    SdkLog.Error($"Skipped an unreadable batch file {Path.GetFileName(batchFilePath)}");
                }
            }

            return loadedBatches;
        }

        // Drops the oldest sealed batches until the outbox is back under its caps.
        private void EnforceCap()
        {
            int droppedEventCount = 0;
            int droppedBatchCount = 0;

            // Totals are read once and counted down, so trimming many batches stays linear.
            int heldEventCount = TotalEventCount;
            long heldByteCount = TotalByteCount;

            while (droppedBatchCount < sealedBatches.Count && IsOverCap(heldEventCount, heldByteCount, sealedBatches.Count - droppedBatchCount))
            {
                SealedBatch oldestBatch = sealedBatches[droppedBatchCount];
                TryDeleteFile(oldestBatch.FilePath);

                heldEventCount -= oldestBatch.EventCount;
                heldByteCount -= oldestBatch.ByteCount;
                droppedEventCount += oldestBatch.EventCount;
                droppedBatchCount += 1;
            }

            sealedBatches.RemoveRange(0, droppedBatchCount);

            if (droppedEventCount > 0)
            {
                SdkLog.Warning($"Outbox over its cap; dropped the oldest {droppedEventCount} unsent events");
            }
        }

        // True when any of the three caps is exceeded.
        private bool IsOverCap(int heldEventCount, long heldByteCount, int heldBatchCount)
        {
            return heldEventCount > limits.MaxTotalEventCount
                || heldByteCount > limits.MaxTotalBytes
                || heldBatchCount > limits.MaxSealedBatchCount;
        }

        // Groups event lines into batches under the event-count and byte limits.
        private List<List<ArraySegment<byte>>> ChunkIntoBatches(List<ArraySegment<byte>> eventLines)
        {
            var batches = new List<List<ArraySegment<byte>>>();
            var currentBatch = new List<ArraySegment<byte>>();
            int currentBatchBytes = 0;

            foreach (ArraySegment<byte> eventLine in eventLines)
            {
                // Start a new batch when this line would break either limit.
                int lineBytes = eventLine.Count + 1;
                bool isCurrentBatchFull = currentBatch.Count >= limits.MaxBatchEventCount || currentBatchBytes + lineBytes > limits.MaxBatchBytes;
                if (currentBatch.Count > 0 && isCurrentBatchFull)
                {
                    batches.Add(currentBatch);
                    currentBatch = new List<ArraySegment<byte>>();
                    currentBatchBytes = 0;
                }

                currentBatch.Add(eventLine);
                currentBatchBytes += lineBytes;
            }

            // Keep the final partial batch.
            if (currentBatch.Count > 0)
            {
                batches.Add(currentBatch);
            }

            return batches;
        }

        // Writes event lines as one NDJSON batch file through a temporary file and a rename, and returns its index entry.
        private SealedBatch WriteBatchFile(List<ArraySegment<byte>> eventLines, string batchPath)
        {
            // Every line, including the last, ends in a newline.
            using (var bodyStream = new MemoryStream())
            {
                foreach (ArraySegment<byte> eventLine in eventLines)
                {
                    bodyStream.Write(eventLine.Array, eventLine.Offset, eventLine.Count);
                    bodyStream.WriteByte(NewlineByte);
                }

                // A crash leaves either the whole file or only a temporary one, which launch recovery deletes.
                CreateFoldersIfNeeded();
                string temporaryPath = batchPath + TemporaryFileExtension;
                File.WriteAllBytes(temporaryPath, bodyStream.ToArray());
                File.Move(temporaryPath, batchPath);

                return new SealedBatch(batchPath, eventLines.Count, (int)bodyStream.Length);
            }
        }

        // A unique stamp that sorts oldest first: 13-digit epoch milliseconds, then a 6-digit per-launch sequence.
        private string MakeSortableFileStamp(DateTime nowUtc)
        {
            fileNameSequence += 1;

            long millisecondsSinceEpoch = Math.Max(0, (long)(nowUtc - UnixEpoch).TotalMilliseconds);

            return millisecondsSinceEpoch.ToString("D13", CultureInfo.InvariantCulture) + "-" + fileNameSequence.ToString("D6", CultureInfo.InvariantCulture);
        }

        // A batch file path that sorts oldest first and is not taken yet.
        private string MakeBatchPath(DateTime nowUtc)
        {
            string batchPath;

            // A clock that went backwards could repeat a name from an earlier launch.
            do
            {
                batchPath = Path.Combine(BatchesDirectory, BatchFilePrefix + MakeSortableFileStamp(nowUtc) + BatchFileExtension);
            }
            while (File.Exists(batchPath));

            return batchPath;
        }

        // The file path of one half of a split batch, sorting right where the original did.
        private string MakeHalfPath(SealedBatch batch, int halfNumber)
        {
            string originalStem = Path.GetFileNameWithoutExtension(batch.FilePath);

            return Path.Combine(BatchesDirectory, originalStem + "-" + halfNumber.ToString(CultureInfo.InvariantCulture) + BatchFileExtension);
        }

        // The index of the last newline, or -1.
        private static int LastIndexOfNewline(byte[] bytes)
        {
            for (int byteIndex = bytes.Length - 1; byteIndex >= 0; byteIndex--)
            {
                if (bytes[byteIndex] == NewlineByte)
                {
                    return byteIndex;
                }
            }

            return -1;
        }

        // The first byteCount bytes of an array.
        private static byte[] Slice(byte[] bytes, int byteCount)
        {
            var slice = new byte[byteCount];
            Buffer.BlockCopy(bytes, 0, slice, 0, byteCount);

            return slice;
        }

        // True when a line starts with "{" and ends with "}", ignoring a trailing carriage return.
        private static bool LooksLikeJsonObject(ArraySegment<byte> line)
        {
            int lastIndex = line.Offset + line.Count - 1;
            if (line.Count > 0 && line.Array[lastIndex] == (byte)'\r')
            {
                lastIndex -= 1;
            }

            return lastIndex > line.Offset && line.Array[line.Offset] == (byte)'{' && line.Array[lastIndex] == (byte)'}';
        }
    }
}
