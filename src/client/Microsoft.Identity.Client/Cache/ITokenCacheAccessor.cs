﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Generic;
using Microsoft.Identity.Client.Cache.Items;
using Microsoft.Identity.Client.Cache.Keys;

namespace Microsoft.Identity.Client.Cache
{
    internal interface ITokenCacheAccessor
    {
        void SaveAccessToken(MsalAccessTokenCacheItem item);

        void SaveRefreshToken(MsalRefreshTokenCacheItem item);

        void SaveIdToken(MsalIdTokenCacheItem item);

        void SaveAccount(MsalAccountCacheItem item);

        void SaveAppMetadata(MsalAppMetadataCacheItem item);

        MsalIdTokenCacheItem GetIdToken(MsalAccessTokenCacheItem accessTokenCacheItem);

        MsalAccountCacheItem GetAccount(MsalAccountCacheKey accountKey);

        MsalAppMetadataCacheItem GetAppMetadata(MsalAppMetadataCacheKey appMetadataKey);

        void DeleteAccessToken(MsalAccessTokenCacheItem item);

        void DeleteRefreshToken(MsalRefreshTokenCacheItem item);

        void DeleteIdToken(MsalIdTokenCacheItem item);

        void DeleteAccount(MsalAccountCacheItem item);

        /// <summary>
        /// Returns all access tokens from the underlying cache collection.
        /// If <paramref name="optionalPartitionKey"/> is specified, returns access tokens from that partition only.
        /// </summary>
        /// <remarks>
        /// Not all classes that implement this method are required to filter by partition.
        /// </remarks>
        IReadOnlyList<MsalAccessTokenCacheItem> GetAllAccessTokens(string optionalPartitionKey = null);

        /// <summary>
        /// Returns all refresh tokens from the underlying cache collection.
        /// If <paramref name="optionalPartitionKey"/> is specified, returns refresh tokens from that partition only.
        /// </summary>
        /// <remarks>
        /// Not all classes that implement this method are required to filter by partition.
        /// </remarks>
        IReadOnlyList<MsalRefreshTokenCacheItem> GetAllRefreshTokens(string optionalPartitionKey = null);

        /// <summary>
        /// Returns all ID tokens from the underlying cache collection.
        /// If <paramref name="optionalPartitionKey"/> is specified, returns ID tokens from that partition only.
        /// </summary>
        /// <remarks>
        /// Not all classes that implement this method are required to filter by partition.
        /// </remarks>
        IReadOnlyList<MsalIdTokenCacheItem> GetAllIdTokens(string optionalPartitionKey = null);

        /// <summary>
        /// Returns all accounts from the underlying cache collection.
        /// If <paramref name="optionalPartitionKey"/> is specified, returns accounts from that partition only.
        /// </summary>
        /// <remarks>
        /// Not all classes that implement this method are required to filter by partition.
        /// </remarks>
        IReadOnlyList<MsalAccountCacheItem> GetAllAccounts(string optionalPartitionKey = null);

        IReadOnlyList<MsalAppMetadataCacheItem> GetAllAppMetadata();

#if iOS
        void SetiOSKeychainSecurityGroup(string keychainSecurityGroup);
#endif

        void Clear();

        bool HasAccessOrRefreshTokens();

    }
}
