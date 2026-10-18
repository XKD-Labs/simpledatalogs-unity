#if UNITY_5_3_OR_NEWER
using UnityEngine;

using XKDLabs.SimpleDataLogs.Core;

namespace XKDLabs.SimpleDataLogs
{
    /// <summary>Writes SDK log lines to the Unity console, each prefixed with <c>[simpledatalogs]</c>.</summary>
    internal sealed class UnityLogSink : ISdkLogSink
    {
        /// <summary>Writes one line at the matching console level.</summary>
        public void Write(SdkLogLevel level, string message)
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
