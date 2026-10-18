using System;
using System.Collections.Generic;
using System.Globalization;

namespace XKDLabs.SimpleDataLogs.Core
{
    /// <summary>Turns loosely typed properties into the four JSON kinds ingest accepts, dropping the rest with a log line.</summary>
    internal static class PropertyNormalizer
    {
        /// <summary>
        /// Copies the supported properties: strings, booleans, integers (as long or ulong) and finite floating-point numbers (as double).
        /// </summary>
        public static Dictionary<string, object> Normalize(IDictionary<string, object> rawProperties, string eventName)
        {
            var normalizedProperties = new Dictionary<string, object>(StringComparer.Ordinal);
            if (rawProperties == null)
            {
                return normalizedProperties;
            }

            foreach (KeyValuePair<string, object> rawProperty in rawProperties)
            {
                // A property needs a name to become a JSON key.
                if (string.IsNullOrEmpty(rawProperty.Key))
                {
                    SdkLog.Warning($"Dropped a property of {eventName} with an empty name");
                    continue;
                }

                // Keep supported values; log every dropped one so the host can fix its call site.
                if (TryNormalizeValue(rawProperty.Value, out object normalizedValue, out string dropReason))
                {
                    normalizedProperties[rawProperty.Key] = normalizedValue;
                }
                else
                {
                    SdkLog.Warning($"Dropped property {rawProperty.Key} of {eventName}: {dropReason}");
                }
            }

            return normalizedProperties;
        }

        /// <summary>Merges event properties over defaults, so the call site wins on a clash.</summary>
        public static Dictionary<string, object> Merge(IDictionary<string, object> defaultProperties, IDictionary<string, object> eventProperties)
        {
            var mergedProperties = new Dictionary<string, object>(defaultProperties, StringComparer.Ordinal);

            foreach (KeyValuePair<string, object> eventProperty in eventProperties)
            {
                mergedProperties[eventProperty.Key] = eventProperty.Value;
            }

            return mergedProperties;
        }

        // Maps one raw value onto string, long, ulong, double or bool, or explains why it cannot be sent.
        private static bool TryNormalizeValue(object rawValue, out object normalizedValue, out string dropReason)
        {
            normalizedValue = null;
            dropReason = null;

            switch (rawValue)
            {
                case null:
                    dropReason = "null is not a supported value";
                    return false;
                case string stringValue:
                    normalizedValue = stringValue;
                    return true;
                case char charValue:
                    normalizedValue = charValue.ToString();
                    return true;
                case bool boolValue:
                    normalizedValue = boolValue;
                    return true;
                case int intValue:
                    normalizedValue = (long)intValue;
                    return true;
                case long longValue:
                    normalizedValue = longValue;
                    return true;
                case short shortValue:
                    normalizedValue = (long)shortValue;
                    return true;
                case sbyte sbyteValue:
                    normalizedValue = (long)sbyteValue;
                    return true;
                case byte byteValue:
                    normalizedValue = (long)byteValue;
                    return true;
                case ushort ushortValue:
                    normalizedValue = (long)ushortValue;
                    return true;
                case uint uintValue:
                    normalizedValue = (long)uintValue;
                    return true;
                case ulong ulongValue:
                    normalizedValue = ulongValue <= long.MaxValue ? (object)(long)ulongValue : ulongValue;
                    return true;
                case float floatValue:
                    // Going through the shortest text keeps 0.1f as 0.1 instead of 0.10000000149011612.
                    return TryNormalizeDouble(FloatToDouble(floatValue), out normalizedValue, out dropReason);
                case double doubleValue:
                    return TryNormalizeDouble(doubleValue, out normalizedValue, out dropReason);
                case decimal decimalValue:
                    normalizedValue = (double)decimalValue;
                    return true;
                default:
                    dropReason = $"values of type {rawValue.GetType().Name} are not supported (use a string, number or boolean)";
                    return false;
            }
        }

        // Keeps finite doubles; NaN and infinity are not valid JSON.
        private static bool TryNormalizeDouble(double doubleValue, out object normalizedValue, out string dropReason)
        {
            normalizedValue = null;
            dropReason = null;

            if (double.IsNaN(doubleValue) || double.IsInfinity(doubleValue))
            {
                dropReason = "NaN and infinity are not valid JSON";
                return false;
            }

            normalizedValue = doubleValue;
            return true;
        }

        // Widens a float through its round-trip text, so the double prints the digits the developer wrote.
        private static double FloatToDouble(float floatValue)
        {
            if (float.IsNaN(floatValue) || float.IsInfinity(floatValue))
            {
                return floatValue;
            }

            string roundTripText = floatValue.ToString("R", CultureInfo.InvariantCulture);

            return double.Parse(roundTripText, NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}
