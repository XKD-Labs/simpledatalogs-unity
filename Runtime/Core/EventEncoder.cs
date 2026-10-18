using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace XKDLabs.SimpleDataLogs.Core
{
    /// <summary>A hand-written JSON encoder for events, safe under IL2CPP (no reflection).</summary>
    internal static class EventEncoder
    {
        /// <summary>The <c>ts</c> layout: ISO 8601 in UTC with millisecond precision.</summary>
        public const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

        /// <summary>The JavaScript line separator, escaped so lines stay safe to embed in scripts.</summary>
        private const char LineSeparator = (char)0x2028;

        /// <summary>The JavaScript paragraph separator, escaped for the same reason.</summary>
        private const char ParagraphSeparator = (char)0x2029;

        /// <summary>Stands in for a lone surrogate, which UTF-8 cannot carry.</summary>
        private const char ReplacementCharacter = (char)0xFFFD;

        /// <summary>The hex digits for <c>\u00XX</c> escapes.</summary>
        private const string HexDigits = "0123456789abcdef";

        /// <summary>Encodes an event as one compact JSON object with no newline.</summary>
        public static string EncodeLine(TrackedEvent trackedEvent)
        {
            var lineBuilder = new StringBuilder(256);

            // The fixed fields come first, in the order the ingest docs show them.
            lineBuilder.Append("{\"event\":");
            AppendJsonString(lineBuilder, trackedEvent.EventName);
            lineBuilder.Append(",\"event_id\":");
            AppendJsonString(lineBuilder, trackedEvent.EventId);
            lineBuilder.Append(",\"ts\":");
            AppendJsonString(lineBuilder, FormatTimestamp(trackedEvent.OccurredAtUtc));

            // device_id and session_id only when set.
            if (trackedEvent.DeviceId != null)
            {
                lineBuilder.Append(",\"device_id\":");
                AppendJsonString(lineBuilder, trackedEvent.DeviceId);
            }

            if (trackedEvent.SessionId != null)
            {
                lineBuilder.Append(",\"session_id\":");
                AppendJsonString(lineBuilder, trackedEvent.SessionId);
            }

            // props is always present, with sorted keys so lines stay stable.
            lineBuilder.Append(",\"props\":");
            AppendJsonObject(lineBuilder, trackedEvent.Properties);
            lineBuilder.Append('}');

            return lineBuilder.ToString();
        }

        /// <summary>Formats a time as <c>2026-10-12T09:14:03.120Z</c>.</summary>
        public static string FormatTimestamp(DateTime occurredAt)
        {
            DateTime occurredAtUtc = occurredAt.Kind == DateTimeKind.Local ? occurredAt.ToUniversalTime() : occurredAt;

            return occurredAtUtc.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        }

        /// <summary>Writes a flat object of string, long, ulong, double or bool values, keys sorted.</summary>
        public static void AppendJsonObject(StringBuilder builder, IReadOnlyDictionary<string, object> properties)
        {
            // Sort keys ordinally, so the same props always encode the same way.
            var sortedKeys = new List<string>(properties.Keys);
            sortedKeys.Sort(string.CompareOrdinal);

            builder.Append('{');

            for (int keyIndex = 0; keyIndex < sortedKeys.Count; keyIndex++)
            {
                if (keyIndex > 0)
                {
                    builder.Append(',');
                }

                string propertyKey = sortedKeys[keyIndex];
                AppendJsonString(builder, propertyKey);
                builder.Append(':');
                AppendJsonValue(builder, properties[propertyKey]);
            }

            builder.Append('}');
        }

        /// <summary>Writes one normalized value; anything unexpected becomes its text, so the line stays valid.</summary>
        public static void AppendJsonValue(StringBuilder builder, object propertyValue)
        {
            switch (propertyValue)
            {
                case bool boolValue:
                    builder.Append(boolValue ? "true" : "false");
                    break;
                case long longValue:
                    builder.Append(longValue.ToString(CultureInfo.InvariantCulture));
                    break;
                case ulong ulongValue:
                    builder.Append(ulongValue.ToString(CultureInfo.InvariantCulture));
                    break;
                case double doubleValue:
                    builder.Append(FormatDouble(doubleValue));
                    break;
                case string stringValue:
                    AppendJsonString(builder, stringValue);
                    break;
                default:
                    AppendJsonString(builder, Convert.ToString(propertyValue, CultureInfo.InvariantCulture) ?? string.Empty);
                    break;
            }
        }

        /// <summary>Writes a quoted JSON string; every control character is escaped, so the output never spans lines.</summary>
        public static void AppendJsonString(StringBuilder builder, string text)
        {
            builder.Append('"');

            for (int charIndex = 0; charIndex < text.Length; charIndex++)
            {
                char character = text[charIndex];

                // A surrogate pair passes through whole; a lone surrogate becomes U+FFFD, since it is not valid UTF-8.
                if (char.IsHighSurrogate(character) && charIndex + 1 < text.Length && char.IsLowSurrogate(text[charIndex + 1]))
                {
                    builder.Append(character);
                    builder.Append(text[charIndex + 1]);
                    charIndex++;
                    continue;
                }

                if (char.IsSurrogate(character))
                {
                    builder.Append(ReplacementCharacter);
                    continue;
                }

                AppendEscapedCharacter(builder, character);
            }

            builder.Append('"');
        }

        // Writes one non-surrogate character, escaped when JSON or line safety needs it.
        private static void AppendEscapedCharacter(StringBuilder builder, char character)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    return;
                case '\\':
                    builder.Append("\\\\");
                    return;
                case '\n':
                    builder.Append("\\n");
                    return;
                case '\r':
                    builder.Append("\\r");
                    return;
                case '\t':
                    builder.Append("\\t");
                    return;
                case '\b':
                    builder.Append("\\b");
                    return;
                case '\f':
                    builder.Append("\\f");
                    return;
            }

            // Other control characters, and the two JavaScript line separators, become \uXXXX.
            if (character < 0x20 || character == LineSeparator || character == ParagraphSeparator)
            {
                builder.Append("\\u");
                builder.Append(HexDigits[(character >> 12) & 0xF]);
                builder.Append(HexDigits[(character >> 8) & 0xF]);
                builder.Append(HexDigits[(character >> 4) & 0xF]);
                builder.Append(HexDigits[character & 0xF]);
                return;
            }

            builder.Append(character);
        }

        // Formats a finite double with the shortest text that reads back the same value.
        private static string FormatDouble(double doubleValue)
        {
            // Normalized properties are always finite, but a stray NaN must never break the line.
            if (double.IsNaN(doubleValue) || double.IsInfinity(doubleValue))
            {
                return "null";
            }

            return doubleValue.ToString("R", CultureInfo.InvariantCulture);
        }
    }
}
