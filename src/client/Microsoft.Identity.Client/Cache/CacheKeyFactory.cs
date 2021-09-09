﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using Microsoft.Identity.Client.Cache.Items;
using Microsoft.Identity.Client.Cache.Keys;
using Microsoft.Identity.Client.Internal.Requests;

namespace Microsoft.Identity.Client.Cache
{
    /// <summary>
    /// Responsible for computing:
    /// - external distributed cache key (from request and responses)
    /// - internal cache partition keys (as above, but also from cache items)
    /// 
    /// These are the same string, but MSAL cannot control if the app developer actually uses distributed caching. 
    /// However, MSAL's in-memory cache needs to be partitioned, and this class computes the partition key.
    /// </summary>
    internal static class CacheKeyFactory
    {
        public static string GetKeyFromRequest(AuthenticationRequestParameters requestParameters)
        {
            if (GetOboOrAppKey(requestParameters, out string key))
            {
                return key;
            }

            if (requestParameters.ApiId == TelemetryCore.Internal.Events.ApiEvent.ApiIds.AcquireTokenSilent ||
                requestParameters.ApiId == TelemetryCore.Internal.Events.ApiEvent.ApiIds.RemoveAccount)
            {
                return requestParameters.Account?.HomeAccountId?.Identifier;
            }

            if (requestParameters.ApiId == TelemetryCore.Internal.Events.ApiEvent.ApiIds.GetAccountById)
            {
                return requestParameters.HomeAccountId;
            }

            return null;
        }

        public static string GetKeyFromResponse(AuthenticationRequestParameters requestParameters, string homeAccountIdFromResponse)
        {
            if (GetOboOrAppKey(requestParameters, out string key))
            {
                return key;
            }

            if (requestParameters.IsConfidentialClient ||
                requestParameters.ApiId == TelemetryCore.Internal.Events.ApiEvent.ApiIds.AcquireTokenSilent)
            {
                return homeAccountIdFromResponse;
            }

            return null;
        }

        private static bool GetOboOrAppKey(AuthenticationRequestParameters requestParameters, out string key)
        {
            if (requestParameters.ApiId == TelemetryCore.Internal.Events.ApiEvent.ApiIds.AcquireTokenOnBehalfOf)
            {
                key = requestParameters.UserAssertion.AssertionHash;
                return true;
            }

            if (requestParameters.ApiId == TelemetryCore.Internal.Events.ApiEvent.ApiIds.AcquireTokenForClient)
            {
                string tenantId = requestParameters.Authority.TenantId ?? "";
                key = GetClientCredentialKey(requestParameters.AppConfig.ClientId, tenantId);
                    
                return true;
            }

            key = null;
            return false;
        }

        public static string GetClientCredentialKey(string clientId, string tenantId)
        {                
            return $"{clientId}_{tenantId}_AppTokenCache";
        }

        public static string GetKeyFromCachedItem(MsalAccessTokenCacheItem accessTokenCacheItem)
        {            
            string partitionKey = !string.IsNullOrEmpty(accessTokenCacheItem.UserAssertionHash) ?
              accessTokenCacheItem.UserAssertionHash : 
              accessTokenCacheItem.HomeAccountId;

            return partitionKey;
        }

        public static string GetKeyFromCachedItem(MsalRefreshTokenCacheItem refreshTokenCacheItem)
        {
            string partitionKey = !string.IsNullOrEmpty(refreshTokenCacheItem.UserAssertionHash) ?
              refreshTokenCacheItem.UserAssertionHash : 
              refreshTokenCacheItem.HomeAccountId;

            return partitionKey;
        }

        // Id tokens are not indexed by OBO key, only by home account key
        public static string GetIdTokenKeyFromCachedItem(MsalAccessTokenCacheItem accessTokenCacheItem)
        {
            return accessTokenCacheItem.HomeAccountId;
        }

        public static string GetKeyFromAccount(MsalAccountCacheKey accountKey)
        {
            return accountKey.HomeAccountId;
        }

        public static string GetKeyFromCachedItem(MsalIdTokenCacheItem idTokenCacheItem)
        {
            return idTokenCacheItem.HomeAccountId;
        }

        public static string GetKeyFromCachedItem(MsalAccountCacheItem accountCacheItem)
        {
            return accountCacheItem.HomeAccountId;
        }
    }
}