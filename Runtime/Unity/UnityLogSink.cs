#if UNITY_5_3_OR_NEWER
using System;
using System.Collections.Generic;

using UnityEngine;

using XKDLabs.SimpleDataLogs.Core;

namespace XKDLabs.SimpleDataLogs
{
    /// <summary>Writes SDK log lines to the Unity console, each prefixed with <c>[simpledatalogs]</c>.</summary>
    internal sealed class UnityLogSink : ISdkLogSink
    {
        /// <summary>How many SDK calls this thread is inside; lines wait until it is zero.</summary>
        [ThreadStatic]
        private static int holdDepth;

        /// <summary>Lines logged inside an SDK call, written once the call returns.</summary>
        [ThreadStatic]
        private static List<KeyValuePair<SdkLogLevel, string>> heldLines;

        /// <summary>Holds lines while an SDK call runs, so a host log handler that calls the SDK never re-enters it mid-call.</summary>
        public static void BeginHold()
        {
            holdDepth += 1;
        }

        /// <summary>Ends one hold and writes the held lines once no call is left.</summary>
        public static void EndHold()
        {
            holdDepth = Math.Max(holdDepth - 1, 0);
            if (holdDepth > 0 || heldLines == null)
            {
                return;
            }

            // Take the list first, so a line logged while writing starts a new one.
            List<KeyValuePair<SdkLogLevel, string>> linesToWrite = heldLines;
            heldLines = null;

            foreach (KeyValuePair<SdkLogLevel, string> heldLine in linesToWrite)
            {
                WriteToConsole(heldLine.Key, heldLine.Value);
            }
        }

        /// <summary>Writes one line now, or after the current SDK call.</summary>
        public void Write(SdkLogLevel level, string message)
        {
            if (holdDepth > 0)
            {
                heldLines ??= new List<KeyValuePair<SdkLogLevel, string>>();
                heldLines.Add(new KeyValuePair<SdkLogLevel, string>(level, message));
                return;
            }

            WriteToConsole(level, message);
        }

        // Writes one tagged line at the matching console level.
        private static void WriteToConsole(SdkLogLevel level, string message)
        {
            string taggedMessage = SdkLog.Tag + " " + message;

            switch (level)
            {
                case SdkLogLevel.Error:
                    Debug.LogError(taggedMessage);
                    break;
                case SdkLogLevel.Warning:
                    Debug.LogWarning(taggedMessage);
                    break;
                default:
                    Debug.Log(taggedMessage);
                    break;
            }
        }
    }
}
#endif
