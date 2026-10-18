using System;
using System.Collections.Generic;
using System.Text;

namespace XKDLabs.SimpleDataLogs.Core
{
    /// <summary>
    /// The single-threaded state machine behind the SDK: batching, the disk outbox, and sending with backoff and 401 re-probes.
    /// Every method runs on one thread; only transport callbacks may arrive on another, and they are applied on the next Tick.
    /// </summary>
    internal sealed class EventPipeline
    {
        /// <summary>The most sends one Tick starts, so a synchronous transport cannot spin forever.</summary>
        private const int MaxSendsPerTick = 16;

        /// <summary>Every tunable.</summary>
        private readonly PipelineOptions options;

        /// <summary>Sends one batch.</summary>
        private readonly IIngestTransport transport;

        /// <summary>Supplies the full-jitter fraction in [0, 1).</summary>
        private readonly Func<double> jitterSource;

        /// <summary>Guards the answers transports hand back from other threads.</summary>
        private readonly object completedSendsLock = new object();

        /// <summary>Answers waiting to be applied on the next Tick.</summary>
        private readonly Queue<CompletedSend> completedSends = new Queue<CompletedSend>();

        /// <summary>False after an opt-out.</summary>
        private bool isEnabled;

        /// <summary>True once Start recovered the outbox.</summary>
        private bool hasStarted;

        /// <summary>False once gzip failed here, so it is not tried again this launch.</summary>
        private bool isGzipAvailable = true;

        /// <summary>When a backoff ends, or null when none holds sending.</summary>
        private DateTime? backoffUntilUtc;

        /// <summary>When the next 401 re-probe goes out, while paused.</summary>
        private DateTime? nextProbeAtUtc;

        /// <summary>When the journal's first event arrived, or null when the journal is empty.</summary>
        private DateTime? journalStartedAtUtc;

        /// <summary>Failed sends in a row, which sets the backoff ceiling.</summary>
        private int consecutiveFailureCount;

        /// <summary>Re-probes booked during the current 401 pause.</summary>
        private int invalidKeyProbeCount;

        /// <summary>The send waiting for its answer, or null.</summary>
        private InFlightSend inFlightSend;

        /// <summary>Numbers sends, so a late answer never applies to a newer one.</summary>
        private int lastSendId;

        /// <summary>The batch whose file failed to read most recently.</summary>
        private string unreadableBatchPath;

        /// <summary>How many times in a row that batch failed to read.</summary>
        private int unreadableBatchFailureCount;

        /// <summary>Wires the pipeline to its outbox and transport; nothing touches the disk until Start.</summary>
        public EventPipeline(PipelineOptions options, IIngestTransport transport, Func<double> jitterSource, bool isEnabled)
        {
            this.options = options;
            this.transport = transport;
            this.jitterSource = jitterSource;
            this.isEnabled = isEnabled;
            Outbox = new EventOutbox(options.StorageDirectory, options.BuildOutboxLimits());
        }

        /// <summary>The journal and the sealed batches on disk.</summary>
        public EventOutbox Outbox { get; }

        /// <summary>True while a send waits for its answer.</summary>
        public bool IsRequestInFlight => inFlightSend != null;

        /// <summary>True while a 401 holds sending until a re-probe gets through.</summary>
        public bool IsSendingPaused { get; private set; }

        /// <summary>When the current backoff ends, if one holds sending.</summary>
        public DateTime? BackoffUntilUtc => backoffUntilUtc;

        /// <summary>When the next 401 re-probe goes out, if paused.</summary>
        public DateTime? NextProbeAtUtc => nextProbeAtUtc;

        /// <summary>Failed sends in a row.</summary>
        public int ConsecutiveFailureCount => consecutiveFailureCount;

        /// <summary>Events still waiting in the journal and the sealed batches.</summary>
        public int PendingEventCount => Outbox.TotalEventCount;

        /// <summary>Recovers the outbox from disk; an opted-out user's leftovers are deleted, never sent.</summary>
        public void Start(DateTime nowUtc)
        {
            if (hasStarted)
            {
                return;
            }

            hasStarted = true;

            // Seal leftover journals and index the sealed batches; a broken disk only costs persistence.
            try
            {
                Outbox.RecoverAfterLaunch(nowUtc);
            }
            catch (Exception recoveryError)
            {
                SdkLog.Error($"Could not create the outbox folder: {recoveryError.Message}");
            }

            if (!isEnabled)
            {
                Outbox.DeleteEverything();
            }
        }

        /// <summary>Journals one event, sealing the batch as soon as it is full; the next Tick sends it.</summary>
        public void Track(TrackedEvent trackedEvent, DateTime nowUtc)
        {
            if (!isEnabled || !hasStarted)
            {
                return;
            }

            // Encode now, so a broken event never reaches the disk.
            byte[] lineWithNewline = Encoding.UTF8.GetBytes(EventEncoder.EncodeLine(trackedEvent) + "\n");

            // An event that alone overfills a request body could never be sent.
            if (lineWithNewline.Length > options.MaxRequestBytes)
            {
                SdkLog.Error($"Dropped event {trackedEvent.EventName}: {lineWithNewline.Length} bytes is over the request limit");
                return;
            }

            // Seal first when the journal is full; if that fails, drop this event rather than overfill a request.
            if (Outbox.WouldJournalOverflow(lineWithNewline.Length) && !SealJournal(nowUtc))
            {
                SdkLog.Error($"Dropped event {trackedEvent.EventName}: the full journal could not be sealed");
                return;
            }

            // Journal it, so a crash from here on loses nothing.
            try
            {
                Outbox.AppendToJournal(lineWithNewline);
            }
            catch (Exception appendError)
            {
                SdkLog.Error($"Dropped event {trackedEvent.EventName}: the journal is not writable ({appendError.Message})");
                return;
            }

            // The batch interval counts from the journal's first event.
            if (Outbox.JournalEventCount == 1)
            {
                journalStartedAtUtc = nowUtc;
            }

            // A full batch is sealed now; a partial one waits for the batch interval.
            if (Outbox.JournalEventCount >= options.MaxBatchEventCount)
            {
                SealJournal(nowUtc);
            }
        }

        /// <summary>Turns collection on or off; off stops all sending and deletes every unsent event.</summary>
        public void SetEnabled(bool shouldEnable)
        {
            if (shouldEnable == isEnabled)
            {
                return;
            }

            isEnabled = shouldEnable;

            // Turning collection back on needs nothing more; the next event starts a new batch.
            if (shouldEnable)
            {
                return;
            }

            // Forget every timer and any in-flight request; a late answer is ignored.
            ResetSendingState();

            // An opt-out removes everything not yet delivered.
            Outbox.DeleteEverything();
            journalStartedAtUtc = null;
            SdkLog.Info("Collection disabled; deleted every unsent event");
        }

        /// <summary>Seals the journal and, when asked, sends right away, unless a 401 pause or a backoff says to wait.</summary>
        public void Flush(DateTime nowUtc, bool shouldSendNow)
        {
            if (!isEnabled || !hasStarted)
            {
                return;
            }

            SealJournal(nowUtc);

            if (shouldSendNow)
            {
                Tick(nowUtc);
            }
        }

        /// <summary>Applies answers that arrived, then seals and sends whatever is due; call it often from one thread.</summary>
        public void Tick(DateTime nowUtc)
        {
            if (!hasStarted)
            {
                return;
            }

            for (int sendRound = 0; sendRound < MaxSendsPerTick; sendRound++)
            {
                // Apply the answer to the send in flight, or give up on one that never came.
                ApplyCompletedSends(nowUtc);
                GiveUpOnMissingAnswer(nowUtc);

                if (!isEnabled || inFlightSend != null)
                {
                    return;
                }

                // Stop once nothing is due; a synchronous answer loops for the next batch.
                if (!TrySendNext(nowUtc))
                {
                    return;
                }
            }
        }

        // Forgets backoff, 401 and in-flight state, so nothing lingers after an opt-out.
        private void ResetSendingState()
        {
            inFlightSend = null;
            backoffUntilUtc = null;
            nextProbeAtUtc = null;
            IsSendingPaused = false;
            consecutiveFailureCount = 0;
            invalidKeyProbeCount = 0;
            unreadableBatchPath = null;
            unreadableBatchFailureCount = 0;

            lock (completedSendsLock)
            {
                completedSends.Clear();
            }
        }

        // Seals the journal into a batch; returns false when sealing failed, restarting the batch interval.
        private bool SealJournal(DateTime nowUtc)
        {
            try
            {
                Outbox.SealJournal(nowUtc);
            }
            catch (Exception sealError)
            {
                SdkLog.Error($"Could not seal the journal: {sealError.Message}");
                journalStartedAtUtc = Outbox.JournalEventCount > 0 ? nowUtc : (DateTime?)null;
                return false;
            }

            journalStartedAtUtc = null;
            return true;
        }

        // Starts the next due send; returns true when one began (or a batch was skipped) and another may follow.
        private bool TrySendNext(DateTime nowUtc)
        {
            // While a 401 holds sending, only the scheduled re-probe goes out, with the smallest batch.
            if (IsSendingPaused)
            {
                return TrySendInvalidKeyProbe(nowUtc);
            }

            // A backoff holds every send; when it ends, events journaled meanwhile go out with this attempt.
            if (backoffUntilUtc.HasValue)
            {
                if (nowUtc < backoffUntilUtc.Value)
                {
                    return false;
                }

                backoffUntilUtc = null;
                SealJournal(nowUtc);
            }

            // A partial batch is sealed once it has waited the batch interval.
            if (journalStartedAtUtc.HasValue && nowUtc - journalStartedAtUtc.Value >= options.BatchInterval)
            {
                SealJournal(nowUtc);
            }

            SealedBatch nextBatch = Outbox.OldestSealedBatch;
            if (nextBatch == null)
            {
                return false;
            }

            return BeginSend(nextBatch, nowUtc);
        }

        // Sends the smallest sealed batch once the probe time comes, to learn whether the key works yet.
        private bool TrySendInvalidKeyProbe(DateTime nowUtc)
        {
            if (!nextProbeAtUtc.HasValue || nowUtc < nextProbeAtUtc.Value)
            {
                return false;
            }

            // Events tracked during the pause are probe candidates too.
            SealJournal(nowUtc);

            // With nothing to send there is nothing to learn yet.
            SealedBatch probeBatch = Outbox.SmallestSealedBatch;
            if (probeBatch == null)
            {
                ScheduleInvalidKeyProbe(nowUtc, null);
                return false;
            }

            // The probe time stays booked until an answer replaces it, so a vanished batch never strands the pause.
            return BeginSend(probeBatch, nowUtc);
        }

        // Reads a batch and hands it to the transport; returns whether another send may follow in this Tick.
        private bool BeginSend(SealedBatch batch, DateTime nowUtc)
        {
            // A vanished file is skipped; an unreadable one is retried, then set aside.
            byte[] batchBody;
            try
            {
                batchBody = Outbox.ReadBody(batch);
            }
            catch (Exception readError)
            {
                return HandleUnreadableBatch(batch, readError, nowUtc);
            }

            if (batchBody == null)
            {
                return true;
            }

            // A readable batch ends any read-failure streak.
            unreadableBatchPath = null;
            unreadableBatchFailureCount = 0;

            // Remember the send before starting it, since a synchronous transport answers inside Send.
            lastSendId += 1;
            int sendId = lastSendId;
            DateTime answerDeadlineUtc = nowUtc + TimeSpan.FromSeconds(options.RequestTimeoutSeconds) + options.MissingAnswerGrace;
            inFlightSend = new InFlightSend(sendId, batch, batchBody, answerDeadlineUtc);

            // A transport that throws counts as a network error, never as a crash.
            try
            {
                transport.Send(BuildRequest(batchBody), response => EnqueueCompletedSend(sendId, response));
            }
            catch (Exception sendError)
            {
                EnqueueCompletedSend(sendId, IngestResponse.NetworkFailure(sendError.Message));
            }

            return true;
        }

        // Builds the POST: bearer key, NDJSON type, User-Agent where allowed, and gzip for bodies of 1 KB or more.
        private IngestRequest BuildRequest(byte[] batchBody)
        {
            var headers = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Authorization", "Bearer " + options.IngestKey),
                new KeyValuePair<string, string>("Content-Type", SdkInfo.NdjsonContentType),
            };

            if (options.UserAgent != null)
            {
                headers.Add(new KeyValuePair<string, string>("User-Agent", options.UserAgent));
            }

            // Compress when it pays off; a platform without zlib sends uncompressed from then on.
            byte[] requestBody = batchBody;
            if (options.ShouldGzip && isGzipAvailable && batchBody.Length >= BodyCompressor.MinimumCompressedBodyBytes)
            {
                byte[] compressedBody = BodyCompressor.TryGzip(batchBody);
                if (compressedBody == null)
                {
                    isGzipAvailable = false;
                }
                else
                {
                    requestBody = compressedBody;
                    headers.Add(new KeyValuePair<string, string>("Content-Encoding", "gzip"));
                }
            }

            return new IngestRequest(options.IngestUrl, requestBody, headers, options.RequestTimeoutSeconds);
        }

        // Stores an answer from any thread; the next Tick applies it.
        private void EnqueueCompletedSend(int sendId, IngestResponse response)
        {
            lock (completedSendsLock)
            {
                completedSends.Enqueue(new CompletedSend(sendId, response));
            }
        }

        // Applies the answer to the send in flight; answers to older sends are ignored.
        private void ApplyCompletedSends(DateTime nowUtc)
        {
            while (true)
            {
                CompletedSend completedSend;
                lock (completedSendsLock)
                {
                    if (completedSends.Count == 0)
                    {
                        return;
                    }

                    completedSend = completedSends.Dequeue();
                }

                if (inFlightSend == null || completedSend.SendId != inFlightSend.SendId)
                {
                    continue;
                }

                InFlightSend finishedSend = inFlightSend;
                inFlightSend = null;
                ApplyOutcome(finishedSend, IngestOutcome.Classify(completedSend.Response, nowUtc), nowUtc);
            }
        }

        // Treats a send whose answer never came as a network error, so a stuck transport cannot stall the outbox.
        private void GiveUpOnMissingAnswer(DateTime nowUtc)
        {
            if (inFlightSend == null || nowUtc < inFlightSend.AnswerDeadlineUtc)
            {
                return;
            }

            InFlightSend abandonedSend = inFlightSend;
            inFlightSend = null;
            IngestOutcome missingAnswer = IngestOutcome.Classify(IngestResponse.NetworkFailure("no answer before the deadline"), nowUtc);
            ApplyOutcome(abandonedSend, missingAnswer, nowUtc);
        }

        // Applies the ingest response rule for one answered batch.
        private void ApplyOutcome(InFlightSend finishedSend, IngestOutcome outcome, DateTime nowUtc)
        {
            SealedBatch batch = finishedSend.Batch;

            switch (outcome.Kind)
            {
                case IngestOutcomeKind.Accepted:
                    EndInvalidKeyPauseIfNeeded();
                    consecutiveFailureCount = 0;
                    Outbox.Delete(batch);
                    break;
                case IngestOutcomeKind.InvalidKey:
                    PauseForInvalidKey(nowUtc);
                    break;
                case IngestOutcomeKind.PayloadTooLarge:
                    // Ingest authenticates before it weighs the body, so a 413 also proves the key works.
                    EndInvalidKeyPauseIfNeeded();
                    SplitTooLargeBatch(batch, finishedSend.Body, nowUtc);
                    break;
                case IngestOutcomeKind.Unsupported:
                    // Resending it unchanged can never work, so it is set aside and the next batch goes.
                    EndInvalidKeyPauseIfNeeded();
                    SdkLog.Error($"Ingest refused a batch of {batch.EventCount} events as unsupported (415); it was set aside");
                    Outbox.SetAside(batch, EventOutbox.UnsupportedFileExtension);
                    break;
                default:
                    HandleFailedSend(nowUtc, outcome.RetryAfterSeconds, outcome.Reason);
                    break;
            }
        }

        // Splits a batch the server called too large; a failed half-write backs off and tries again later.
        private void SplitTooLargeBatch(SealedBatch batch, byte[] batchBody, DateTime nowUtc)
        {
            try
            {
                BatchSplitOutcome splitOutcome = Outbox.SplitInHalf(batch, batchBody);

                if (splitOutcome == BatchSplitOutcome.Split)
                {
                    SdkLog.Warning($"Ingest answered 413; split a batch of {batch.EventCount} events in half");
                }
                else if (splitOutcome == BatchSplitOutcome.DroppedLoneEvent)
                {
                    SdkLog.Error("Ingest answered 413 for a single event; dropped it because it can never be accepted");
                }
            }
            catch (Exception splitError)
            {
                HandleFailedSend(nowUtc, null, $"the halves of a too-large batch could not be written ({splitError.Message})");
            }
        }

        // Retries an unreadable batch a few times, then sets it aside so it cannot block newer batches.
        private bool HandleUnreadableBatch(SealedBatch batch, Exception readError, DateTime nowUtc)
        {
            // Count failures in a row on this same batch.
            bool isSameBatchAsLastFailure = unreadableBatchPath == batch.FilePath;
            unreadableBatchFailureCount = isSameBatchAsLastFailure ? unreadableBatchFailureCount + 1 : 1;
            unreadableBatchPath = batch.FilePath;

            // After a few tries the batch is set aside, and the next one may go at once.
            if (unreadableBatchFailureCount >= options.MaxBatchReadAttempts)
            {
                SdkLog.Error($"Set aside a batch of {batch.EventCount} events that failed to read {unreadableBatchFailureCount} times in a row");
                Outbox.SetAside(batch, EventOutbox.UnreadableFileExtension);
                unreadableBatchPath = null;
                unreadableBatchFailureCount = 0;
                return true;
            }

            // Otherwise wait and read it again, like any failed send.
            HandleFailedSend(nowUtc, null, $"an unreadable batch file ({readError.Message})");
            return false;
        }

        // Backs off after a failed send; during a 401 pause it books the next probe instead, since the key is still unproven.
        private void HandleFailedSend(DateTime nowUtc, double? retryAfterSeconds, string reason)
        {
            if (IsSendingPaused)
            {
                // A probe lost in transit says nothing about the key, so the pause holds.
                SdkLog.Warning($"Key re-probe got no answer ({reason}); sending stays paused");
                ScheduleInvalidKeyProbe(nowUtc, retryAfterSeconds);
                return;
            }

            // Work out the wait from the failure streak and any server hint, and hold every send until then.
            consecutiveFailureCount += 1;
            double retryDelaySeconds = options.RetryPolicy.DelaySeconds(consecutiveFailureCount, retryAfterSeconds, jitterSource());
            backoffUntilUtc = nowUtc + TimeSpan.FromSeconds(retryDelaySeconds);

            SdkLog.Warning($"Send failed ({reason}); retrying in {retryDelaySeconds:0.0} s");
        }

        // Pauses sending after a 401, keeping every event, and books a re-probe.
        private void PauseForInvalidKey(DateTime nowUtc)
        {
            // Log the configuration error once, when the pause begins.
            if (!IsSendingPaused)
            {
                SdkLog.Error("Ingest rejected the key (401); sending is paused and events are kept. Check the key passed to Configure.");
            }

            IsSendingPaused = true;
            backoffUntilUtc = null;
            ScheduleInvalidKeyProbe(nowUtc, null);
        }

        // Books the next 401 re-probe: a minute after the first rejection, then every 10 minutes, or later if Retry-After says so.
        private void ScheduleInvalidKeyProbe(DateTime nowUtc, double? retryAfterSeconds)
        {
            double probeDelaySeconds = options.RetryPolicy.InvalidKeyProbeDelaySeconds(invalidKeyProbeCount, retryAfterSeconds);
            invalidKeyProbeCount += 1;
            nextProbeAtUtc = nowUtc + TimeSpan.FromSeconds(probeDelaySeconds);
        }

        // Ends a 401 pause once a 202, 413 or 415 shows the key is accepted again.
        private void EndInvalidKeyPauseIfNeeded()
        {
            if (!IsSendingPaused)
            {
                return;
            }

            IsSendingPaused = false;
            invalidKeyProbeCount = 0;
            nextProbeAtUtc = null;
            SdkLog.Info("Ingest accepted the key again; sending resumed");
        }

        /// <summary>A send waiting for its answer.</summary>
        private sealed class InFlightSend
        {
            /// <summary>Records one send.</summary>
            public InFlightSend(int sendId, SealedBatch batch, byte[] body, DateTime answerDeadlineUtc)
            {
                SendId = sendId;
                Batch = batch;
                Body = body;
                AnswerDeadlineUtc = answerDeadlineUtc;
            }

            /// <summary>The send's number.</summary>
            public int SendId { get; }

            /// <summary>The batch that was sent.</summary>
            public SealedBatch Batch { get; }

            /// <summary>The uncompressed body, kept for a 413 split.</summary>
            public byte[] Body { get; }

            /// <summary>When a missing answer counts as a network error.</summary>
            public DateTime AnswerDeadlineUtc { get; }
        }

        /// <summary>An answer a transport handed back.</summary>
        private sealed class CompletedSend
        {
            /// <summary>Pairs an answer with its send.</summary>
            public CompletedSend(int sendId, IngestResponse response)
            {
                SendId = sendId;
                Response = response;
            }

            /// <summary>The send this answers.</summary>
            public int SendId { get; }

            /// <summary>What came back.</summary>
            public IngestResponse Response { get; }
        }
    }
}
