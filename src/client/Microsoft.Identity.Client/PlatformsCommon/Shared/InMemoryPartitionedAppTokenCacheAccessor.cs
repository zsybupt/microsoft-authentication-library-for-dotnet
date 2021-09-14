// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Identity.Client.Cache;
using Microsoft.Identity.Client.Cache.Items;
using Microsoft.Identity.Client.Cache.Keys;
using Microsoft.Identity.Client.Core;

namespace Microsoft.Identity.Client.PlatformsCommon.Shared
{
    /// <summary>
    /// Stores tokens for an application.
    /// Partitions the access token collection by a key of client ID with tenant ID.
    /// App metadata collection is not partitioned.
    /// Refresh token, ID token, and account related methods are no-op.
    /// </summary>
    internal class InMemoryPartitionedAppTokenCacheAccessor : ITokenCacheAccessor
    {
        // perf: do not use ConcurrentDictionary.Values as it takes a lock
        internal /* internal for test only */ readonly ConcurrentDictionary<string, ConcurrentDictionary<string, MsalAccessTokenCacheItem>> AccessTokenCacheDictionary;
        internal /* internal for test only */  readonly ConcurrentDictionary<string, MsalAppMetadataCacheItem> AppMetadataDictionary;

        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, MsalAccessTokenCacheItem>> s_accessTokenCacheDictionary =
            new ConcurrentDictionary<string, ConcurrentDictionary<string, MsalAccessTokenCacheItem>>();

        private static readonly ConcurrentDictionary<string, MsalAppMetadataCacheItem> s_appMetadataDictionary =
           new ConcurrentDictionary<string, MsalAppMetadataCacheItem>(1, 1);


        protected readonly ICoreLogger _logger;
        private readonly InternalMemoryTokenCacheOptions _tokenCacheAccessorOptions;

        public InMemoryPartitionedAppTokenCacheAccessor(
            ICoreLogger logger, 
            InternalMemoryTokenCacheOptions tokenCacheAccessorOptions)
        {
            _logger = logger ?? throw new System.ArgumentNullException(nameof(logger));
            _tokenCacheAccessorOptions = tokenCacheAccessorOptions ?? new InternalMemoryTokenCacheOptions();

            if (_tokenCacheAccessorOptions.UseSharedCache)
            {
                AccessTokenCacheDictionary = s_accessTokenCacheDictionary;              
                AppMetadataDictionary = s_appMetadataDictionary;
            }
            else
            {
                AccessTokenCacheDictionary = new ConcurrentDictionary<string, ConcurrentDictionary<string, MsalAccessTokenCacheItem>>();
                AppMetadataDictionary = new ConcurrentDictionary<string, MsalAppMetadataCacheItem>();
            }
        }

        #region Add
        public void SaveAccessToken(MsalAccessTokenCacheItem item)
        {
            var partitionKey = CacheKeyFactory.GetClientCredentialKey(item.ClientId, item.TenantId);
            string itemKey = item.GetKey().ToString();
            // if a conflict occurs, pick the latest value
            AccessTokenCacheDictionary
                .GetOrAdd(partitionKey, new ConcurrentDictionary<string, MsalAccessTokenCacheItem>())[itemKey] = item;
        }

        public void SaveRefreshToken(MsalRefreshTokenCacheItem item)
        {
        }

        public void SaveIdToken(MsalIdTokenCacheItem item)
        {
        }

        public void SaveAccount(MsalAccountCacheItem item)
        {
        }

        public void SaveAppMetadata(MsalAppMetadataCacheItem item)
        {
            string key = item.GetKey().ToString();
            AppMetadataDictionary[key] = item;
        }
        #endregion

        #region Get            

        public MsalAccountCacheItem GetAccount(MsalAccountCacheKey accountKey)
        {
            return null;
        }

        public MsalAppMetadataCacheItem GetAppMetadata(MsalAppMetadataCacheKey appMetadataKey)
        {
            AppMetadataDictionary.TryGetValue(appMetadataKey.ToString(), out MsalAppMetadataCacheItem cacheItem);
            return cacheItem;
        }
        #endregion

        #region Delete
        public void DeleteAccessToken(MsalAccessTokenCacheItem item)
        {
            var partitionKey = CacheKeyFactory.GetClientCredentialKey(item.ClientId, item.TenantId);

            AccessTokenCacheDictionary.TryGetValue(partitionKey, out var partition);
            if (partition == null || !partition.TryRemove(item.GetKey().ToString(), out _))
            {
                _logger.InfoPii(
                    $"Cannot delete access token because it was not found in the cache. Key {item.GetKey()}.",
                    "Cannot delete access token because it was not found in the cache.");
            }
        }

        public void DeleteRefreshToken(MsalRefreshTokenCacheItem item)
        {
            throw new NotImplementedException();
        }

        public void DeleteIdToken(MsalIdTokenCacheItem item)
        {
            throw new NotImplementedException();
        }

        public void DeleteAccount(MsalAccountCacheItem item)
        {
            throw new NotImplementedException();
        }


        #endregion

        #region Get All
        public IReadOnlyList<MsalAccessTokenCacheItem> GetAllAccessTokens(string partitionKey = null)
        {
            if (string.IsNullOrEmpty(partitionKey))
            {
                return AccessTokenCacheDictionary.SelectMany(dict => dict.Value).Select(kv => kv.Value).ToList();
            }
            else
            {
                AccessTokenCacheDictionary.TryGetValue(partitionKey, out ConcurrentDictionary<string, MsalAccessTokenCacheItem> partition);
                return partition?.Select(kv => kv.Value)?.ToList() ?? new List<MsalAccessTokenCacheItem>();
            }
        }

        public IReadOnlyList<MsalRefreshTokenCacheItem> GetAllRefreshTokens(string partitionKey = null)
        {
            return new List<MsalRefreshTokenCacheItem>();
        }

        public IReadOnlyList<MsalIdTokenCacheItem> GetAllIdTokens(string partitionKey = null)
        {
            return new List<MsalIdTokenCacheItem>();
        }

        public IReadOnlyList<MsalAccountCacheItem> GetAllAccounts(string partitionKey = null)
        {
            return new List<MsalAccountCacheItem>();
        }

        public IReadOnlyList<MsalAppMetadataCacheItem> GetAllAppMetadata()
        {
            return AppMetadataDictionary.Select(kv => kv.Value).ToList();
        }
        #endregion

        public void SetiOSKeychainSecurityGroup(string keychainSecurityGroup)
        {
            throw new System.NotImplementedException();
        }

        public virtual void Clear()
        {
            AccessTokenCacheDictionary.Clear();
            // app metadata isn't removable
        }

        public bool HasAccessOrRefreshTokens()
        {
            return AccessTokenCacheDictionary.Any(partition => partition.Value.Any(token => !IsAtExpired(token.Value)));
        }

        private bool IsAtExpired(MsalAccessTokenCacheItem at)
        {
            return at.ExpiresOn < DateTime.UtcNow + Internal.Constants.AccessTokenExpirationBuffer;
        }

        public MsalIdTokenCacheItem GetIdToken(MsalAccessTokenCacheItem accessTokenCacheItem)
        {
            return null; 
        }

       
    }
}
