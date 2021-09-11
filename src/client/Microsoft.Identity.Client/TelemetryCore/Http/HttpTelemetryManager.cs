﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Text;
using Microsoft.Identity.Client.TelemetryCore.Internal.Constants;
using Microsoft.Identity.Client.TelemetryCore.Internal.Events;

namespace Microsoft.Identity.Client.TelemetryCore.Http
{
    /// <summary>
    /// Responsible for recording API events and formatting CSV 
    /// with telemetry.
    /// </summary>
    /// <remarks>
    /// Not fully thread safe - it is possible that multiple threads request
    /// the "previous requests" data at the same time. It is the responsibility of 
    /// the caller to protect against this.
    /// </remarks>
    internal class HttpTelemetryManager : IHttpTelemetryManager
    {
        private int _successfullSilentCallCount = 0;
        private ConcurrentQueue<ApiEvent> _failedEvents = new ConcurrentQueue<ApiEvent>();

        public void ResetPreviousUnsentData()
        {
            _successfullSilentCallCount = 0;
            while (_failedEvents.TryDequeue(out _))
            {
                // do nothing
            }
        }

        public void RecordStoppedEvent(ApiEvent stoppedEvent)
        {
            if (!string.IsNullOrEmpty(stoppedEvent.ApiErrorCode))
            {
                _failedEvents.Enqueue(stoppedEvent);
            }

            // cache hits can occur in AcquireTokenSilent, AcquireTokenForClient and OBO
            if (stoppedEvent.IsAccessTokenCacheHit)
            {
                _successfullSilentCallCount++;
            }
        }

        /// <summary>
        /// CSV expected format:
        ///      3|silent_successful_count|failed_requests|errors|platform_fields
        ///      failed_request is: api_id_1,correlation_id_1,api_id_2,correlation_id_2|error_1,error_2
        /// </summary>
        public string GetLastRequestHeader()
        {
            var failedRequests = new StringBuilder();
            var errors = new StringBuilder();
            bool firstFailure = true;

            foreach (var ev in _failedEvents)
            {
                if (!firstFailure)
                    errors.Append(",");

                errors.Append(
                    // error codes come from the server / broker and can sometimes be full blown sentences,
                    // with punctuation that is illegal in an HTTP Header 
                    HttpHeaderSantizer.SantizeHeader(ev.ApiErrorCode));

                if (!firstFailure)
                    failedRequests.Append(",");

                failedRequests.Append(ev.ApiIdString);
                failedRequests.Append(",");
                failedRequests.Append(ev.CorrelationId);

                firstFailure = false;
            }

            string data =
                $"{TelemetryConstants.HttpTelemetrySchemaVersion}|" +
                $"{_successfullSilentCallCount}|" +
                $"{failedRequests}|" +
                $"{errors}|";

            // TODO: fix this
            if (data.Length > 3800)
            {
                ResetPreviousUnsentData();
                return string.Empty;
            }

            return data;
        }

        /// <summary>
        /// Expected format: 5|api_id,cache_info,region_used,region_autodetection,region_outcome|platform_config
        /// platform_config: is_token_cache_serialized,is_legacy_cache_enabled
        /// </summary>
        public string GetCurrentRequestHeader(ApiEvent eventInProgress)
        {
            if (eventInProgress == null)
            {
                return string.Empty;
            }

            eventInProgress.TryGetValue(MsalTelemetryBlobEventNames.ApiIdConstStrKey, out string apiId);
            eventInProgress.TryGetValue(MsalTelemetryBlobEventNames.CacheInfoKey, out string cacheInfo);
            eventInProgress.TryGetValue(MsalTelemetryBlobEventNames.RegionUsed, out string regionUsed);
            eventInProgress.TryGetValue(MsalTelemetryBlobEventNames.RegionSource, out string regionSource);
            eventInProgress.TryGetValue(MsalTelemetryBlobEventNames.RegionOutcome, out string regionOutcome);
            eventInProgress.TryGetValue(MsalTelemetryBlobEventNames.IsTokenCacheSerializedKey, out string isTokenCacheSerialized);
            eventInProgress.TryGetValue(MsalTelemetryBlobEventNames.IsLegacyCacheEnabledKey, out string isLegacyCacheEnabled);

            var platformConfig = new StringBuilder();
            platformConfig.Append(ConvertFromStringToBitwise(isTokenCacheSerialized) + ",");
            platformConfig.Append(ConvertFromStringToBitwise(isLegacyCacheEnabled));

            return $"{TelemetryConstants.HttpTelemetrySchemaVersion}" +
                $"|{apiId},{cacheInfo},{regionUsed},{regionSource},{regionOutcome}" +
                $"|{platformConfig}";
        }

        private string ConvertFromStringToBitwise(string value)
        {
            if (string.IsNullOrEmpty(value) || value == TelemetryConstants.False)
            {
                return TelemetryConstants.Zero;
            }

            return TelemetryConstants.One;
        }
    }
}
