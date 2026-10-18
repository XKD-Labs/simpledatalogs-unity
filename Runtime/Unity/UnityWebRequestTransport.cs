#if UNITY_5_3_OR_NEWER
using System;
using System.Collections.Generic;

using UnityEngine.Networking;

using XKDLabs.SimpleDataLogs.Core;

namespace XKDLabs.SimpleDataLogs
{
    /// <summary>Sends batches with UnityWebRequest, which works on every player, including WebGL and IL2CPP.</summary>
    internal sealed class UnityWebRequestTransport : IIngestTransport
    {
        /// <summary>Starts the POST on the main thread; the answer arrives through the operation's completed event.</summary>
        public void Send(IngestRequest request, Action<IngestResponse> onComplete)
        {
            var webRequest = new UnityWebRequest(request.Url, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(request.Body) { contentType = SdkInfo.NdjsonContentType },
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = request.TimeoutSeconds,
                useHttpContinue = false,
            };

            // Every header the pipeline chose: Authorization, Content-Type, and User-Agent and Content-Encoding where allowed.
            foreach (KeyValuePair<string, string> header in request.Headers)
            {
                webRequest.SetRequestHeader(header.Key, header.Value);
            }

            UnityWebRequestAsyncOperation sendOperation = webRequest.SendWebRequest();
            sendOperation.completed += completedOperation => Complete(webRequest, onComplete);
        }

        // Classifies the finished request, frees it, and hands the answer back.
        private static void Complete(UnityWebRequest webRequest, Action<IngestResponse> onComplete)
        {
            IngestResponse response;

            try
            {
                response = ToResponse(webRequest);
            }
            catch (Exception readError)
            {
                response = IngestResponse.NetworkFailure(readError.Message);
            }
            finally
            {
                webRequest.Dispose();
            }

            onComplete(response);
        }

        // An HTTP status when one arrived; otherwise a network failure (offline, DNS, timeout, CORS).
        private static IngestResponse ToResponse(UnityWebRequest webRequest)
        {
            bool hasHttpStatus = webRequest.result != UnityWebRequest.Result.ConnectionError && webRequest.responseCode > 0;
            if (!hasHttpStatus)
            {
                return IngestResponse.NetworkFailure(webRequest.error);
            }

            return IngestResponse.FromStatus((int)webRequest.responseCode, webRequest.GetResponseHeader("Retry-After"));
        }
    }
}
#endif
