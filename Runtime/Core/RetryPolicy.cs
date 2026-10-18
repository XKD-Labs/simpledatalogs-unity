using System;
using System.Globalization;

namespace XKDLabs.SimpleDataLogs.Core
{
    /// <summary>Exponential backoff with full jitter, from 1 s up to 5 min, that never retries sooner than Retry-After.</summary>
    internal sealed class RetryPolicy
    {
        /// <summary>The backoff ceiling after the first failure.</summary>
        public double InitialDelaySeconds { get; set; } = 1;

        /// <summary>The largest backoff ceiling (5 min).</summary>
        public double MaximumDelaySeconds { get; set; } = 300;

        /// <summary>The shortest wait, so a jitter draw near zero can never spin the send loop.</summary>
        public double MinimumDelaySeconds { get; set; } = 0.25;

        /// <summary>The longest Retry-After honored, so a broken header cannot stall sending for days.</summary>
        public double MaximumRetryAfterSeconds { get; set; } = 3600;

        /// <summary>The first re-probe after a 401, soon enough to catch a brand-new key going live.</summary>
        public double InvalidKeyFirstProbeDelaySeconds { get; set; } = 60;

        /// <summary>Every later re-probe while the key keeps answering 401.</summary>
        public double InvalidKeyProbeIntervalSeconds { get; set; } = 600;

        /// <summary>The jitter-free ceiling after this many failures in a row: 1 s, 2 s, 4 s … 5 min.</summary>
        public double BackoffCeilingSeconds(int failureCount)
        {
            // Cap the exponent so the doubling can never overflow.
            int doublingCount = Math.Min(Math.Max(failureCount - 1, 0), 30);

            return Math.Min(MaximumDelaySeconds, InitialDelaySeconds * Math.Pow(2, doublingCount));
        }

        /// <summary>The wait before the next attempt: full jitter, at least 250 ms, never less than Retry-After.</summary>
        public double DelaySeconds(int failureCount, double? retryAfterSeconds, double jitterFraction)
        {
            // Full jitter picks uniformly between zero and the ceiling, then the floor applies.
            double clampedJitterFraction = Math.Min(Math.Max(jitterFraction, 0), 1);
            double jitteredBackoff = Math.Max(MinimumDelaySeconds, BackoffCeilingSeconds(failureCount) * clampedJitterFraction);

            return HonoringRetryAfter(retryAfterSeconds, jitteredBackoff);
        }

        /// <summary>The wait before the next 401 re-probe, pushed later by a Retry-After from a probe that got 429 or 503.</summary>
        public double InvalidKeyProbeDelaySeconds(int probeCount, double? retryAfterSeconds)
        {
            double scheduledProbeDelay = probeCount == 0 ? InvalidKeyFirstProbeDelaySeconds : InvalidKeyProbeIntervalSeconds;

            return HonoringRetryAfter(retryAfterSeconds, scheduledProbeDelay);
        }

        // Treats the server's Retry-After as a floor over the delay, within a sane bound.
        private double HonoringRetryAfter(double? retryAfterSeconds, double delaySeconds)
        {
            if (!retryAfterSeconds.HasValue)
            {
                return delaySeconds;
            }

            return Math.Max(delaySeconds, Math.Min(retryAfterSeconds.Value, MaximumRetryAfterSeconds));
        }
    }

    /// <summary>Parses an HTTP Retry-After header, given as delay-seconds or an HTTP-date.</summary>
    internal static class RetryAfterHeader
    {
        /// <summary>The HTTP-date layouts recipients accept: IMF-fixdate, RFC 850 and asctime.</summary>
        private static readonly string[] HttpDateFormats =
        {
            "ddd, dd MMM yyyy HH:mm:ss 'GMT'",
            "dddd, dd-MMM-yy HH:mm:ss 'GMT'",
            "ddd MMM d HH:mm:ss yyyy",
        };

        /// <summary>Seconds to wait from now, or null when the header is missing or unreadable; a past date means 0.</summary>
        public static double? ParseDelaySeconds(string headerValue, DateTime nowUtc)
        {
            // A missing or blank header carries no hint.
            string trimmedValue = headerValue?.Trim();
            if (string.IsNullOrEmpty(trimmedValue))
            {
                return null;
            }

            // delay-seconds is a bare non-negative integer.
            if (IsAllAsciiDigits(trimmedValue))
            {
                return double.Parse(trimmedValue, NumberStyles.None, CultureInfo.InvariantCulture);
            }

            // An HTTP-date in the past means retry now.
            if (!TryParseHttpDate(trimmedValue, out DateTime retryAtUtc))
            {
                return null;
            }

            return Math.Max(0, (retryAtUtc - nowUtc).TotalSeconds);
        }

        // True when every character is 0-9.
        private static bool IsAllAsciiDigits(string text)
        {
            foreach (char character in text)
            {
                if (character < '0' || character > '9')
                {
                    return false;
                }
            }

            return true;
        }

        // Parses an HTTP-date in any of the three accepted layouts, always English and always GMT.
        private static bool TryParseHttpDate(string dateText, out DateTime parsedUtc)
        {
            // asctime pads single-digit days with a second space, so collapse runs of spaces first.
            string normalizedDateText = string.Join(" ", dateText.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));

            return DateTime.TryParseExact(
                normalizedDateText,
                HttpDateFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out parsedUtc
            );
        }
    }
}
