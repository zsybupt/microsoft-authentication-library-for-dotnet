﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Identity.Client.AuthScheme.Bearer;
using Microsoft.Identity.Client.Cache;
using Microsoft.Identity.Client.Cache.Items;
using Microsoft.Identity.Client.Cache.Keys;
using Microsoft.Identity.Client.Instance;
using Microsoft.Identity.Client.Instance.Discovery;
using Microsoft.Identity.Client.Internal;
using Microsoft.Identity.Client.Internal.Requests;
using Microsoft.Identity.Client.OAuth2;
using Microsoft.Identity.Client.TelemetryCore.Internal.Events;
using Microsoft.Identity.Client.Utils;

namespace Microsoft.Identity.Client
{
    /// <summary>
    /// IMPORTANT: this class is perf critical; any changes must be benchmarked using Microsoft.Identity.Test.Performace.
    /// More information about how to test and what data to look for is in https://aka.ms/msal-net-performance-testing.
    /// </summary>
    public sealed partial class TokenCache : ITokenCacheInternal
    {

        #region SaveTokenResponse
        async Task<Tuple<MsalAccessTokenCacheItem, MsalIdTokenCacheItem, Account>> ITokenCacheInternal.SaveTokenResponseAsync(
            AuthenticationRequestParameters requestParams,
            MsalTokenResponse response)
        {
            response.Log(requestParams.RequestContext.Logger, LogLevel.Verbose);

            MsalAccessTokenCacheItem msalAccessTokenCacheItem = null;
            MsalRefreshTokenCacheItem msalRefreshTokenCacheItem = null;
            MsalIdTokenCacheItem msalIdTokenCacheItem = null;
            MsalAccountCacheItem msalAccountCacheItem = null;

            IdToken idToken = IdToken.Parse(response.IdToken);
            if (idToken == null)
            {
                requestParams.RequestContext.Logger.Info("ID Token not present in response. ");
            }

            var tenantId = GetTenantId(idToken, requestParams);

            bool isAdfsAuthority = requestParams.AuthorityInfo.AuthorityType == AuthorityType.Adfs;
            bool isAadAuthority = requestParams.AuthorityInfo.AuthorityType == AuthorityType.Aad;
            string preferredUsername = GetPreferredUsernameFromIdToken(isAdfsAuthority, idToken);
            string username = isAdfsAuthority ? idToken?.Upn : preferredUsername;
            string homeAccountId = GetHomeAccountId(requestParams, response, idToken);
            string suggestedWebCacheKey = SuggestedWebCacheKeyFactory.GetKeyFromResponse(requestParams, homeAccountId);

            // Do a full instance discovery when saving tokens (if not cached),
            // so that the PreferredNetwork environment is up to date.
            InstanceDiscoveryMetadataEntry instanceDiscoveryMetadata =
                await requestParams.AuthorityManager.GetInstanceDiscoveryEntryAsync().ConfigureAwait(false);

            #region Create Cache Objects
            if (!string.IsNullOrEmpty(response.AccessToken))
            {
                msalAccessTokenCacheItem =
                    new MsalAccessTokenCacheItem(
                        instanceDiscoveryMetadata.PreferredCache,
                        requestParams.AppConfig.ClientId,
                        response,
                        tenantId,
                        homeAccountId,
                        requestParams.AuthenticationScheme.KeyId)
                    {
                        UserAssertionHash = requestParams.UserAssertion?.AssertionHash,
                        IsAdfs = isAdfsAuthority
                    };
            }

            if (!string.IsNullOrEmpty(response.RefreshToken))
            {
                msalRefreshTokenCacheItem = new MsalRefreshTokenCacheItem(
                                    instanceDiscoveryMetadata.PreferredCache,
                                    requestParams.AppConfig.ClientId,
                                    response,
                                    homeAccountId)
                {
                    UserAssertionHash = requestParams.UserAssertion?.AssertionHash
                };

                if (!_featureFlags.IsFociEnabled)
                {
                    msalRefreshTokenCacheItem.FamilyId = null;
                }
            }

            Account account = null;
            if (idToken != null)
            {
                msalIdTokenCacheItem = new MsalIdTokenCacheItem(
                    instanceDiscoveryMetadata.PreferredCache,
                    requestParams.AppConfig.ClientId,
                    response,
                    tenantId,
                    homeAccountId);

                Dictionary<string, string> wamAccountIds = GetWamAccountIds(requestParams, response);
                msalAccountCacheItem = new MsalAccountCacheItem(
                             instanceDiscoveryMetadata.PreferredCache,
                             response.ClientInfo,
                             homeAccountId,
                             idToken,
                             preferredUsername,
                             tenantId,
                             wamAccountIds);

                // Add the newly obtained id token to the list of profiles
                var tenantProfiles = await (this as ITokenCacheInternal).GetTenantProfilesAsync(requestParams, homeAccountId).ConfigureAwait(false);
                if (isAadAuthority && tenantProfiles != null)
                {
                    TenantProfile tenantProfile = new TenantProfile(msalIdTokenCacheItem);
                    tenantProfiles[msalIdTokenCacheItem.TenantId] = tenantProfile;
                }

                account = new Account(
                  homeAccountId,
                  username,
                  instanceDiscoveryMetadata.PreferredNetwork,
                  wamAccountIds,
                  tenantProfiles?.Values);
            }

            #endregion

            requestParams.RequestContext.Logger.Verbose($"[SaveTokenResponseAsync] Entering token cache semaphore. Count {_semaphoreSlim.GetCurrentCountLogMessage()}.");
            await _semaphoreSlim.WaitAsync(requestParams.RequestContext.UserCancellationToken).ConfigureAwait(false);
            requestParams.RequestContext.Logger.Verbose("[SaveTokenResponseAsync] Entered token cache semaphore. ");
            ITokenCacheInternal tokenCacheInternal = this;

            try
            {
#pragma warning disable CS0618 // Type or member is obsolete
                HasStateChanged = true;
#pragma warning restore CS0618 // Type or member is obsolete

                try
                {
                    if (tokenCacheInternal.IsExternalSerializationEnabled())
                    {
                        var args = new TokenCacheNotificationArgs(
                            this,
                            ClientId,
                            account,
                            hasStateChanged: true,
                            tokenCacheInternal.IsApplicationCache,
                            hasTokens: tokenCacheInternal.HasTokensNoLocks(suggestedWebCacheKey),
                            requestParams.RequestContext.UserCancellationToken,
                            suggestedCacheKey: suggestedWebCacheKey);

                        Stopwatch sw = Stopwatch.StartNew();

                        await tokenCacheInternal.OnBeforeAccessAsync(args).ConfigureAwait(false);
                        await tokenCacheInternal.OnBeforeWriteAsync(args).ConfigureAwait(false);
                        requestParams.RequestContext.ApiEvent.DurationInCacheInMs += sw.ElapsedMilliseconds;
                    }

                    if (msalAccessTokenCacheItem != null)
                    {
                        requestParams.RequestContext.Logger.Info("Saving AT in cache and removing overlapping ATs...");

                        DeleteAccessTokensWithIntersectingScopes(
                            requestParams,
                            instanceDiscoveryMetadata.Aliases,
                            tenantId,
                            msalAccessTokenCacheItem.ScopeSet,
                            msalAccessTokenCacheItem.HomeAccountId,
                            msalAccessTokenCacheItem.TokenType);

                        _accessor.SaveAccessToken(msalAccessTokenCacheItem);
                    }

                    if (idToken != null)
                    {
                        requestParams.RequestContext.Logger.Info("Saving Id Token and Account in cache ...");
                        _accessor.SaveIdToken(msalIdTokenCacheItem);
                        MergeWamAccountIds(msalAccountCacheItem);
                        _accessor.SaveAccount(msalAccountCacheItem);
                    }

                    // if server returns the refresh token back, save it in the cache.
                    if (msalRefreshTokenCacheItem != null)
                    {
                        requestParams.RequestContext.Logger.Info("Saving RT in cache...");
                        _accessor.SaveRefreshToken(msalRefreshTokenCacheItem);
                    }

                    UpdateAppMetadata(requestParams.AppConfig.ClientId, instanceDiscoveryMetadata.PreferredCache, response.FamilyId);

                    SaveToLegacyAdalCache(requestParams, response, msalRefreshTokenCacheItem, msalIdTokenCacheItem, tenantId, instanceDiscoveryMetadata);
                }
                finally
                {
                    if (tokenCacheInternal.IsExternalSerializationEnabled())
                    {
                        DateTimeOffset? cacheExpiry = null;

                        if (!_accessor.GetAllRefreshTokens(suggestedWebCacheKey).Any())
                        {
                            cacheExpiry = CalculateSuggestedCacheExpiry(suggestedWebCacheKey);
                        }

                        var args = new TokenCacheNotificationArgs(
                            this,
                            ClientId,
                            account,
                            hasStateChanged: true,
                            tokenCacheInternal.IsApplicationCache,
                            tokenCacheInternal.HasTokensNoLocks(suggestedWebCacheKey),
                            requestParams.RequestContext.UserCancellationToken,
                            suggestedCacheKey: suggestedWebCacheKey,
                            suggestedCacheExpiry: cacheExpiry);

                        Stopwatch sw = Stopwatch.StartNew();
                        await tokenCacheInternal.OnAfterAccessAsync(args).ConfigureAwait(false);
                        requestParams.RequestContext.ApiEvent.DurationInCacheInMs += sw.ElapsedMilliseconds;
                    }
#pragma warning disable CS0618 // Type or member is obsolete
                    HasStateChanged = false;
#pragma warning restore CS0618 // Type or member is obsolete
                }

                return Tuple.Create(msalAccessTokenCacheItem, msalIdTokenCacheItem, account);
            }
            finally
            {
                _semaphoreSlim.Release();
                requestParams.RequestContext.Logger.Verbose("[SaveTokenResponseAsync] Released token cache semaphore. ");
            }
        }

        private bool IsLegacyAdalCacheEnabled(AuthenticationRequestParameters requestParams)
        {
            if (requestParams.IsClientCredentialRequest)
            {
                // client_credentials request. Only RTs are transferable between ADAL and MSAL
                return false;
            }

            if (ServiceBundle.PlatformProxy.LegacyCacheRequiresSerialization &&
               !(this as ITokenCacheInternal).IsExternalSerializationConfigured())
            {
                // serialization is not configured but is required
                return false;
            }

            if (!ServiceBundle.Config.LegacyCacheCompatibilityEnabled)
            {
                // disabled by app developer
                return false;
            }

            if (requestParams.AuthorityInfo.AuthorityType == AuthorityType.B2C)
            {
                // ADAL did not support B2C
                return false;
            }

            requestParams.RequestContext.Logger.Info("IsLegacyAdalCacheEnabled: yes");
            return true;
        }

        private void SaveToLegacyAdalCache(
            AuthenticationRequestParameters requestParams,
            MsalTokenResponse response,
            MsalRefreshTokenCacheItem msalRefreshTokenCacheItem,
            MsalIdTokenCacheItem msalIdTokenCacheItem,
            string tenantId,
            InstanceDiscoveryMetadataEntry instanceDiscoveryMetadata)
        {
            if (IsLegacyAdalCacheEnabled(requestParams))
            {
                requestParams.RequestContext.Logger.Info("Saving to ADAL legacy cache. ");

                var tenatedAuthority = Authority.CreateAuthorityWithTenant(requestParams.AuthorityInfo, tenantId);
                var authorityWithPreferredCache = Authority.CreateAuthorityWithEnvironment(
                        tenatedAuthority.AuthorityInfo,
                        instanceDiscoveryMetadata.PreferredCache);

                CacheFallbackOperations.WriteAdalRefreshToken(
                    requestParams.RequestContext.Logger,
                    LegacyCachePersistence,
                    msalRefreshTokenCacheItem,
                    msalIdTokenCacheItem,
                    authorityWithPreferredCache.AuthorityInfo.CanonicalAuthority,
                    msalIdTokenCacheItem.IdToken.ObjectId,
                    response.Scope);
            }
        }

        private DateTimeOffset CalculateSuggestedCacheExpiry(string partitionKey)
        {
            IEnumerable<MsalAccessTokenCacheItem> tokenCacheItems = GetAllAccessTokensWithNoLocks(true, partitionKey);
            var unixCacheExpiry = tokenCacheItems.Max(item => item.ExpiresOnUnixTimestamp);
            return (DateTimeOffset)CoreHelpers.UnixTimestampStringToDateTime(unixCacheExpiry);
        }

        private string GetTenantId(IdToken idToken, AuthenticationRequestParameters requestParams)
        {
            // If the input authority was tenanted, use that tenant over the IdToken.Tenant
            // otherwise, this will result in cache misses
            return Authority.CreateAuthorityWithTenant(
                requestParams.Authority.AuthorityInfo,
                idToken?.TenantId).TenantId;
        }

        private void MergeWamAccountIds(MsalAccountCacheItem msalAccountCacheItem)
        {
            var existingAccount = _accessor.GetAccount(msalAccountCacheItem.GetKey());
            var existingWamAccountIds = existingAccount?.WamAccountIds;
            msalAccountCacheItem.WamAccountIds.MergeDifferentEntries(existingWamAccountIds);
        }

        private static Dictionary<string, string> GetWamAccountIds(AuthenticationRequestParameters requestParams, MsalTokenResponse response)
        {
            if (!string.IsNullOrEmpty(response.WamAccountId))
            {
                return new Dictionary<string, string>() { { requestParams.AppConfig.ClientId, response.WamAccountId } };
            }

            return new Dictionary<string, string>();
        }

        private static string GetHomeAccountId(AuthenticationRequestParameters requestParams, MsalTokenResponse response, IdToken idToken)
        {
            ClientInfo clientInfo = response.ClientInfo != null ? ClientInfo.CreateFromJson(response.ClientInfo) : null;
            string homeAccountId = clientInfo?.ToAccountIdentifier() ?? idToken?.Subject; // ADFS does not have client info, so we use subject

            if (homeAccountId == null)
            {
                requestParams.RequestContext.Logger.Info("Cannot determine home account id - or id token or no client info and no subject ");
            }
            return homeAccountId;
        }

        private static string GetPreferredUsernameFromIdToken(bool isAdfsAuthority, IdToken idToken)
        {
            // The preferred_username value cannot be null or empty in order to comply with the ADAL/MSAL Unified cache schema.
            // It will be set to "preferred_username not in id token"
            if (idToken == null)
            {
                return NullPreferredUsernameDisplayLabel;
            }

            if (string.IsNullOrWhiteSpace(idToken.PreferredUsername))
            {
                if (isAdfsAuthority)
                {
                    //The direct to ADFS scenario does not return preferred_username in the id token so it needs to be set to the UPN
                    return !string.IsNullOrEmpty(idToken.Upn)
                        ? idToken.Upn
                        : NullPreferredUsernameDisplayLabel;
                }
                return NullPreferredUsernameDisplayLabel;
            }

            return idToken.PreferredUsername;
        }
        #endregion

        #region FindAccessToken
        /// <summary>
        /// IMPORTANT: this class is perf critical; any changes must be benchmarked using Microsoft.Identity.Test.Performace.
        /// More information about how to test and what data to look for is in https://aka.ms/msal-net-performance-testing.
        /// 
        /// Scenario: client_creds with default in-memory cache can get to ~500k tokens
        /// </summary>
        async Task<MsalAccessTokenCacheItem> ITokenCacheInternal.FindAccessTokenAsync(
            AuthenticationRequestParameters requestParams)
        {
            var logger = requestParams.RequestContext.Logger;

            // no authority passed
            if (requestParams.AuthorityInfo?.CanonicalAuthority == null)
            {
                logger.Warning("FindAccessToken: No authority provided. Skipping cache lookup. ");
                return null;
            }

            // take a snapshot of the access tokens to avoid problems where the underlying collection is changed,
            // as this method is NOT locked by the semaphore
            string partitionKey = SuggestedWebCacheKeyFactory.GetKeyFromRequest(requestParams);
            IReadOnlyList<MsalAccessTokenCacheItem> tokenCacheItems = GetAllAccessTokensWithNoLocks(true, partitionKey);
            if (tokenCacheItems.Count == 0)
            {
                logger.Verbose("No access tokens found in the cache. Skipping filtering. ");
                requestParams.RequestContext.ApiEvent.CacheInfo = (int)CacheInfoTelemetry.NoCachedAT;
                return null;
            }

            tokenCacheItems = FilterByHomeAccountTenantOrAssertion(requestParams, tokenCacheItems);
            tokenCacheItems = FilterByTokenType(requestParams, tokenCacheItems);
            tokenCacheItems = FilterByScopes(requestParams, tokenCacheItems);
            tokenCacheItems = await FilterByEnvironmentAsync(requestParams, tokenCacheItems).ConfigureAwait(false);

            CacheInfoTelemetry cacheInfoTelemetry = CacheInfoTelemetry.None;

            // no match
            if (tokenCacheItems.Count == 0)
            {
                logger.Verbose("No tokens found for matching authority, client_id, user and scopes. ");
                return null;
            }

            MsalAccessTokenCacheItem msalAccessTokenCacheItem = GetSingleResult(requestParams, tokenCacheItems);
            msalAccessTokenCacheItem = FilterByKeyId(msalAccessTokenCacheItem, requestParams);
            msalAccessTokenCacheItem = FilterByExpiry(msalAccessTokenCacheItem, requestParams);

            if (msalAccessTokenCacheItem == null)
            {
                cacheInfoTelemetry = CacheInfoTelemetry.Expired;
            }

            requestParams.RequestContext.ApiEvent.CacheInfo = (int)cacheInfoTelemetry;

            return msalAccessTokenCacheItem;
        }

        private static IReadOnlyList<MsalAccessTokenCacheItem> FilterByScopes(
            AuthenticationRequestParameters requestParams,
            IReadOnlyList<MsalAccessTokenCacheItem> tokenCacheItems)
        {
            var logger = requestParams.RequestContext.Logger;
            if (tokenCacheItems.Count == 0)
            {
                logger.Verbose("Not filtering by scopes, because there are no candidates");
                return tokenCacheItems;
            }

            var requestScopes = requestParams.Scope.Where(s =>
                !OAuth2Value.ReservedScopes.Contains(s));

            tokenCacheItems = tokenCacheItems.FilterWithLogging(
                item =>
                {
                    bool accepted = ScopeHelper.ScopeContains(item.ScopeSet, requestScopes);

                    if (logger.IsLoggingEnabled(LogLevel.Verbose))
                    {
                        logger.Verbose($"Access token with scopes {string.Join(" ", item.ScopeSet)} " +
                            $"passes scope filter? {accepted} ");
                    }
                    return accepted;
                },
                logger,
                "Filtering by scopes");

            return tokenCacheItems;
        }

        private static IReadOnlyList<MsalAccessTokenCacheItem> FilterByTokenType(
            AuthenticationRequestParameters requestParams,
            IReadOnlyList<MsalAccessTokenCacheItem> tokenCacheItems)
        {
            tokenCacheItems = tokenCacheItems.FilterWithLogging(item =>
                            string.Equals(
                                item.TokenType ?? BearerAuthenticationScheme.BearerTokenType,
                                requestParams.AuthenticationScheme.AccessTokenType,
                                StringComparison.OrdinalIgnoreCase),
                            requestParams.RequestContext.Logger,
                            "Filtering by token type");
            return tokenCacheItems;
        }

        private static IReadOnlyList<MsalAccessTokenCacheItem> FilterByHomeAccountTenantOrAssertion(
            AuthenticationRequestParameters requestParams,
            IReadOnlyList<MsalAccessTokenCacheItem> tokenCacheItems)
        {
            string requestTenantId = requestParams.Authority.TenantId;
            bool filterByTenantId = true;

            if (requestParams.ApiId == ApiEvent.ApiIds.AcquireTokenOnBehalfOf) // OBO
            {
                tokenCacheItems =
                    tokenCacheItems.FilterWithLogging(item =>
                        !string.IsNullOrEmpty(item.UserAssertionHash) &&
                        item.UserAssertionHash.Equals(requestParams.UserAssertion.AssertionHash, StringComparison.OrdinalIgnoreCase),
                        requestParams.RequestContext.Logger,
                        $"Filtering AT by user assertion: {requestParams.UserAssertion.AssertionHash}");

                // OBO calls FindAccessTokenAsync directly, but we are not able to resolve the authority 
                // unless the developer has configured a tenanted authority. If they have configured /common
                // then we cannot filter by tenant and will use whatever is in the cache.
                filterByTenantId =
                    !string.IsNullOrEmpty(requestTenantId) &&
                    !AadAuthority.IsCommonOrganizationsOrConsumersTenant(requestTenantId);
            }

            if (filterByTenantId)
            {
                tokenCacheItems = tokenCacheItems.FilterWithLogging(item =>
                    string.Equals(item.TenantId ?? string.Empty, requestTenantId ?? string.Empty, StringComparison.OrdinalIgnoreCase),
                    requestParams.RequestContext.Logger,
                    "Filtering AT by tenant id");
            }
            else
            {
                requestParams.RequestContext.Logger.Warning("Have not filtered by tenant ID. " +
                    "This can happen in OBO scenario where authority is /common or /organizations. " +
                    "Please use tenanted authority.");
            }

            // Only AcquireTokenSilent has an IAccount in the request that can be used for filtering
            if (requestParams.ApiId != ApiEvent.ApiIds.AcquireTokenForClient &&
                requestParams.ApiId != ApiEvent.ApiIds.AcquireTokenOnBehalfOf)
            {
                tokenCacheItems = tokenCacheItems.FilterWithLogging(item => item.HomeAccountId.Equals(
                                requestParams.Account.HomeAccountId?.Identifier, StringComparison.OrdinalIgnoreCase),
                                requestParams.RequestContext.Logger,
                                "Filtering AT by home account id");
            }

            return tokenCacheItems;
        }

        private MsalAccessTokenCacheItem FilterByExpiry(MsalAccessTokenCacheItem msalAccessTokenCacheItem, AuthenticationRequestParameters requestParams)
        {
            var logger = requestParams.RequestContext.Logger;
            if (msalAccessTokenCacheItem != null)
            {

                if (msalAccessTokenCacheItem.ExpiresOn > DateTime.UtcNow + AccessTokenExpirationBuffer)
                {
                    // due to https://github.com/AzureAD/microsoft-authentication-library-for-dotnet/issues/1806
                    if (msalAccessTokenCacheItem.ExpiresOn > DateTime.UtcNow + TimeSpan.FromDays(ExpirationTooLongInDays))
                    {
                        logger.Error(
                           "Access token expiration too large. This can be the result of a bug or corrupt cache. Token will be ignored as it is likely expired." +
                           GetAccessTokenExpireLogMessageContent(msalAccessTokenCacheItem));
                        return null;
                    }

                    if (logger.IsLoggingEnabled(LogLevel.Info))
                    {
                        logger.Info(
                            "Access token is not expired. Returning the found cache entry. " +
                            GetAccessTokenExpireLogMessageContent(msalAccessTokenCacheItem));
                    }

                    return msalAccessTokenCacheItem;
                }

                if (ServiceBundle.Config.IsExtendedTokenLifetimeEnabled &&
                    msalAccessTokenCacheItem.ExtendedExpiresOn > DateTime.UtcNow + AccessTokenExpirationBuffer)
                {
                    if (logger.IsLoggingEnabled(LogLevel.Info))
                    {
                        logger.Info(
                            "Access token is expired.  IsExtendedLifeTimeEnabled=TRUE and ExtendedExpiresOn is not exceeded.  Returning the found cache entry. " +
                            GetAccessTokenExpireLogMessageContent(msalAccessTokenCacheItem));
                    }

                    msalAccessTokenCacheItem.IsExtendedLifeTimeToken = true;
                    return msalAccessTokenCacheItem;
                }

                if (logger.IsLoggingEnabled(LogLevel.Info))
                {
                    logger.Info(
                        "Access token has expired or about to expire. " +
                        GetAccessTokenExpireLogMessageContent(msalAccessTokenCacheItem));
                }
            }

            return null;
        }

        private static MsalAccessTokenCacheItem GetSingleResult(
            AuthenticationRequestParameters requestParams,
            IReadOnlyList<MsalAccessTokenCacheItem> filteredItems)
        {
            // if only one cached token found
            if (filteredItems.Count == 1)
            {
                return filteredItems[0];
            }

            requestParams.RequestContext.Logger.Error("Multiple access tokens found for matching authority, client_id, user and scopes. ");
            throw new MsalClientException(
                MsalError.MultipleTokensMatchedError,
                MsalErrorMessage.MultipleTokensMatched);
        }

        private async Task<IReadOnlyList<MsalAccessTokenCacheItem>> FilterByEnvironmentAsync(
            AuthenticationRequestParameters requestParams,
            IReadOnlyList<MsalAccessTokenCacheItem> filteredItems)
        {
            var logger = requestParams.RequestContext.Logger;

            if (filteredItems.Count == 0)
            {
                logger.Verbose("Not filtering AT by env, because there are no candidates");
                return filteredItems;
            }

            // at this point we need env aliases, try to get them without a discovery call
            var instanceMetadata = await ServiceBundle.InstanceDiscoveryManager.GetMetadataEntryTryAvoidNetworkAsync(
                                     requestParams.AuthorityInfo,
                                     filteredItems.Select(at => at.Environment),  // if all environments are known, a network call can be avoided
                                     requestParams.RequestContext)
                            .ConfigureAwait(false);

            // In case we're sharing the cache with an MSAL that does not implement env aliasing,
            // it's possible (but unlikely), that we have multiple ATs from the same alias family.
            // To overcome some of these use cases, try to filter just by preferred cache alias
            var filteredByPreferredAlias = filteredItems.FilterWithLogging(
                item => item.Environment.Equals(instanceMetadata.PreferredCache, StringComparison.OrdinalIgnoreCase),
                requestParams.RequestContext.Logger,
                $"Filtering AT by preferred environment {instanceMetadata.PreferredCache}");

            if (filteredByPreferredAlias.Count > 0)
            {
                if (logger.IsLoggingEnabled(LogLevel.Verbose))
                {
                    logger.Verbose($"Filtered AT by preferred alias returning {filteredByPreferredAlias.Count} tokens");
                }

                return filteredByPreferredAlias;
            }

            return filteredItems.FilterWithLogging(
                item => instanceMetadata.Aliases.ContainsOrdinalIgnoreCase(item.Environment),
                requestParams.RequestContext.Logger,
                $"Filtering AT by environment");
        }

        private MsalAccessTokenCacheItem FilterByKeyId(MsalAccessTokenCacheItem item, AuthenticationRequestParameters authenticationRequest)
        {
            if (item == null)
            {
                return null;
            }

            string requestKid = authenticationRequest.AuthenticationScheme.KeyId;
            if (string.IsNullOrEmpty(item.KeyId) && string.IsNullOrEmpty(requestKid))
            {
                authenticationRequest.RequestContext.Logger.Verbose("Bearer token found");
                return item;
            }

            if (string.Equals(item.KeyId, requestKid, StringComparison.OrdinalIgnoreCase))
            {
                authenticationRequest.RequestContext.Logger.Verbose("Keyed token found");
                return item;
            }

            authenticationRequest.RequestContext.Logger.Info(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "A token bound to the wrong key was found. Token key id: {0} Request key id: {1}",
                        item.KeyId,
                        requestKid));
            return null;
        }
        #endregion


        async Task<MsalRefreshTokenCacheItem> ITokenCacheInternal.FindRefreshTokenAsync(
            AuthenticationRequestParameters requestParams,
            string familyId)
        {
            if (requestParams.Authority == null)
                return null;

            IReadOnlyList<MsalRefreshTokenCacheItem> allRts = _accessor.GetAllRefreshTokens(SuggestedWebCacheKeyFactory.GetKeyFromRequest(requestParams));
            if (allRts.Count != 0)
            {
                var metadata =
                    await ServiceBundle.InstanceDiscoveryManager.GetMetadataEntryTryAvoidNetworkAsync(
                        requestParams.AuthorityInfo,
                        allRts.Select(rt => rt.Environment),  // if all environments are known, a network call can be avoided
                        requestParams.RequestContext)
                    .ConfigureAwait(false);
                var aliases = metadata.Aliases;

                allRts = FilterRtsByHomeAccountIdOrAssertion(requestParams, allRts, familyId);
                allRts = allRts.Where(
                    item => aliases.ContainsOrdinalIgnoreCase(item.Environment)).ToList();

                IReadOnlyList<MsalRefreshTokenCacheItem> finalList = allRts.ToList();
                requestParams.RequestContext.Logger.Info("Refresh token found in the cache? - " + (finalList.Count != 0));

                if (finalList.Count > 0)
                {
                    return finalList.FirstOrDefault();
                }
            }
            else
            {
                requestParams.RequestContext.Logger.Verbose("No RTs found in the MSAL cache ");
            }

            requestParams.RequestContext.Logger.Verbose("Checking ADAL cache for matching RT. ");

            if (IsLegacyAdalCacheEnabled(requestParams) &&
                requestParams.Account != null &&
                string.IsNullOrEmpty(familyId)) // ADAL legacy cache does not store FRTs
            {
                var metadata =
                  await ServiceBundle.InstanceDiscoveryManager.GetMetadataEntryTryAvoidNetworkAsync(
                      requestParams.AuthorityInfo,
                      allRts.Select(rt => rt.Environment),  // if all environments are known, a network call can be avoided
                      requestParams.RequestContext)
                  .ConfigureAwait(false);
                var aliases = metadata.Aliases;

                return CacheFallbackOperations.GetRefreshToken(
                    requestParams.RequestContext.Logger,
                    LegacyCachePersistence,
                    aliases,
                    requestParams.AppConfig.ClientId,
                    requestParams.Account);
            }

            return null;
        }

        private static IReadOnlyList<MsalRefreshTokenCacheItem> FilterRtsByHomeAccountIdOrAssertion(
            AuthenticationRequestParameters requestParams,
            IReadOnlyList<MsalRefreshTokenCacheItem> rtCacheItems,
            string familyId)
        {
            if (requestParams.ApiId == ApiEvent.ApiIds.AcquireTokenOnBehalfOf) // OBO
            {
                rtCacheItems = rtCacheItems.FilterWithLogging(item =>
                                !string.IsNullOrEmpty(item.UserAssertionHash) &&
                                item.UserAssertionHash.Equals(requestParams.UserAssertion.AssertionHash, StringComparison.OrdinalIgnoreCase),
                                requestParams.RequestContext.Logger,
                                $"Filtering RT by user assertion: {requestParams.UserAssertion.AssertionHash}");
            }
            else
            {
                rtCacheItems = rtCacheItems.FilterWithLogging(item => item.HomeAccountId.Equals(
                                requestParams.Account.HomeAccountId?.Identifier, StringComparison.OrdinalIgnoreCase),
                                requestParams.RequestContext.Logger,
                                "Filtering RT by home account id");
            }

            // This will also filter for the case when familyId is null and exclude RTs with familyId in filtered list
            rtCacheItems = rtCacheItems.FilterWithLogging(item =>
                    string.Equals(item.FamilyId ?? string.Empty,
                    familyId ?? string.Empty, StringComparison.OrdinalIgnoreCase),
                    requestParams.RequestContext.Logger,
                    "Filtering RT by family id");

            // if there is a value in familyId, we are looking for FRT and hence ignore filter with clientId
            if (string.IsNullOrEmpty(familyId))
            {
                rtCacheItems = rtCacheItems.FilterWithLogging(item => item.ClientId.Equals(
                            requestParams.AppConfig.ClientId, StringComparison.OrdinalIgnoreCase),
                            requestParams.RequestContext.Logger,
                            "Filtering RT by client id");
            }

            return rtCacheItems;
        }

        async Task<bool?> ITokenCacheInternal.IsFociMemberAsync(AuthenticationRequestParameters requestParams, string familyId)
        {
            var logger = requestParams.RequestContext.Logger;
            if (requestParams?.AuthorityInfo?.CanonicalAuthority == null)
            {
                logger.Warning("No authority details, can't check app metadata. Returning unknown. ");
                return null;
            }

            IEnumerable<MsalAppMetadataCacheItem> allAppMetadata = _accessor.GetAllAppMetadata();

            var instanceMetadata = await ServiceBundle.InstanceDiscoveryManager.GetMetadataEntryTryAvoidNetworkAsync(
                    requestParams.AuthorityInfo,
                    allAppMetadata.Select(m => m.Environment),
                    requestParams.RequestContext)
                .ConfigureAwait(false);

            var appMetadata =
                instanceMetadata.Aliases
                .Select(env => _accessor.GetAppMetadata(new MsalAppMetadataCacheKey(ClientId, env)))
                .FirstOrDefault(item => item != null);

            // From a FOCI perspective, an app has 3 states - in the family, not in the family or unknown
            // Unknown is a valid state, where we never fetched tokens for that app or when we used an older
            // version of MSAL which did not record app metadata.
            if (appMetadata == null)
            {
                logger.Warning("No app metadata found. Returning unknown. ");
                return null;
            }

            return appMetadata.FamilyId == familyId;
        }



        /// <remarks>
        /// Get accounts should not make a network call, if possible. This can be achieved if
        /// all the environments in the token cache are known to MSAL, as MSAL keeps a list of
        /// known environments in <see cref="KnownMetadataProvider"/>
        /// </remarks>
        async Task<IEnumerable<IAccount>> ITokenCacheInternal.GetAccountsAsync(AuthenticationRequestParameters requestParameters)
        {
            var logger = requestParameters.RequestContext.Logger;
            var environment = requestParameters.AuthorityInfo.Host;
            bool filterByClientId = !_featureFlags.IsFociEnabled;
            bool isAadAuthority = requestParameters.AuthorityInfo.AuthorityType == AuthorityType.Aad;

            // this will either be the home account id or null, it can never be obo assertion or tenant id
            string partitionkey = SuggestedWebCacheKeyFactory.GetKeyFromRequest(requestParameters);

            IReadOnlyList<MsalRefreshTokenCacheItem> rtCacheItems = GetAllRefreshTokensWithNoLocks(filterByClientId, partitionkey);
            IReadOnlyList<MsalAccountCacheItem> accountCacheItems = _accessor.GetAllAccounts(partitionkey);

            if (logger.IsLoggingEnabled(LogLevel.Verbose))
                logger.Verbose($"GetAccounts found {rtCacheItems.Count} RTs and {accountCacheItems.Count} accounts in MSAL cache. ");

            // Multi-cloud support - must filter by env.
            ISet<string> allEnvironmentsInCache = new HashSet<string>(
                accountCacheItems.Select(aci => aci.Environment),
                StringComparer.OrdinalIgnoreCase);
            allEnvironmentsInCache.UnionWith(rtCacheItems.Select(rt => rt.Environment));

            AdalUsersForMsal adalUsersResult = null;

            if (IsLegacyAdalCacheEnabled(requestParameters))
            {
                adalUsersResult = CacheFallbackOperations.GetAllAdalUsersForMsal(
                    logger,
                    LegacyCachePersistence,
                    ClientId);
                allEnvironmentsInCache.UnionWith(adalUsersResult.GetAdalUserEnviroments());
            }

            InstanceDiscoveryMetadataEntry instanceMetadata = await ServiceBundle.InstanceDiscoveryManager.GetMetadataEntryTryAvoidNetworkAsync(
                requestParameters.AuthorityInfo,
                allEnvironmentsInCache,
                requestParameters.RequestContext).ConfigureAwait(false);

            rtCacheItems = rtCacheItems.Where(rt => instanceMetadata.Aliases.ContainsOrdinalIgnoreCase(rt.Environment)).ToList();
            accountCacheItems = accountCacheItems.Where(acc => instanceMetadata.Aliases.ContainsOrdinalIgnoreCase(acc.Environment)).ToList();

            if (logger.IsLoggingEnabled(LogLevel.Verbose))
                logger.Verbose($"GetAccounts found {rtCacheItems.Count} RTs and {accountCacheItems.Count} accounts in MSAL cache after environment filtering. ");

            IDictionary<string, Account> clientInfoToAccountMap = new Dictionary<string, Account>();
            foreach (MsalRefreshTokenCacheItem rtItem in rtCacheItems)
            {
                foreach (MsalAccountCacheItem account in accountCacheItems)
                {
                    if (RtMatchesAccount(rtItem, account))
                    {
                        var tenantProfiles = await (this as ITokenCacheInternal).GetTenantProfilesAsync(requestParameters, account.HomeAccountId).ConfigureAwait(false);

                        clientInfoToAccountMap[rtItem.HomeAccountId] = new Account(
                            account.HomeAccountId,
                            account.PreferredUsername,
                            environment, // Preserve the env passed in by the user
                            account.WamAccountIds,
                            tenantProfiles?.Values);

                        break;
                    }
                }
            }

            if (IsLegacyAdalCacheEnabled(requestParameters))
            {
                UpdateMapWithAdalAccountsWithClientInfo(
                    environment,
                    instanceMetadata.Aliases,
                    adalUsersResult,
                    clientInfoToAccountMap);
            }

            // Add WAM accounts stored in MSAL's cache - for which we do not have an RT
            if (requestParameters.AppConfig.IsBrokerEnabled && ServiceBundle.PlatformProxy.BrokerSupportsWamAccounts)
            {
                foreach (MsalAccountCacheItem wamAccountCache in accountCacheItems.Where(
                    acc => acc.WamAccountIds != null &&
                    acc.WamAccountIds.ContainsKey(requestParameters.AppConfig.ClientId)))
                {
                    var wamAccount = new Account(
                        wamAccountCache.HomeAccountId,
                        wamAccountCache.PreferredUsername,
                        environment,
                        wamAccountCache.WamAccountIds);

                    clientInfoToAccountMap[wamAccountCache.HomeAccountId] = wamAccount;
                }
            }

            var accounts = new List<IAccount>(clientInfoToAccountMap.Values);

            if (IsLegacyAdalCacheEnabled(requestParameters))
            {
                UpdateWithAdalAccountsWithoutClientInfo(environment,
                 instanceMetadata.Aliases,
                 adalUsersResult,
                 accounts);
            }

            if (!string.IsNullOrEmpty(requestParameters.HomeAccountId))
            {
                accounts = accounts.Where(acc => acc.HomeAccountId.Identifier.Equals(
                    requestParameters.HomeAccountId,
                    StringComparison.OrdinalIgnoreCase)).ToList();

                if (logger.IsLoggingEnabled(LogLevel.Verbose))
                    logger.Verbose($"Filtered by home account id. Remaining accounts {accounts.Count()} ");
            }

            return accounts;
        }

        private static void UpdateMapWithAdalAccountsWithClientInfo(
          string envFromRequest,
          IEnumerable<string> envAliases,
          AdalUsersForMsal adalUsers,
          IDictionary<string, Account> clientInfoToAccountMap)
        {
            foreach (KeyValuePair<string, AdalUserInfo> pair in adalUsers?.GetUsersWithClientInfo(envAliases))
            {
                var clientInfo = ClientInfo.CreateFromJson(pair.Key);
                string accountIdentifier = clientInfo.ToAccountIdentifier();

                if (!clientInfoToAccountMap.ContainsKey(accountIdentifier))
                {
                    clientInfoToAccountMap[accountIdentifier] = new Account(
                            accountIdentifier, pair.Value.DisplayableId, envFromRequest);
                }
            }
        }

        private void UpdateWithAdalAccountsWithoutClientInfo(
            string envFromRequest,
            IEnumerable<string> envAliases,
            AdalUsersForMsal adalUsers,
            List<IAccount> accounts)
        {
            var uniqueUserNames = accounts.Select(a => a.Username).Distinct().ToList();

            foreach (AdalUserInfo user in adalUsers?.GetUsersWithoutClientInfo(envAliases))
            {
                if (!string.IsNullOrEmpty(user.DisplayableId) && !uniqueUserNames.Contains(user.DisplayableId))
                {
                    accounts.Add(new Account(null, user.DisplayableId, envFromRequest));
                    uniqueUserNames.Add(user.DisplayableId);
                }
            }
        }

        MsalIdTokenCacheItem ITokenCacheInternal.GetIdTokenCacheItem(MsalIdTokenCacheKey msalIdTokenCacheKey)
        {
            var idToken = _accessor.GetIdToken(msalIdTokenCacheKey);
            return idToken;
        }

        async Task<IDictionary<string, TenantProfile>> ITokenCacheInternal.GetTenantProfilesAsync(
            AuthenticationRequestParameters requestParameters,
            string homeAccountId)
        {
            if (requestParameters.AuthorityInfo.AuthorityType != AuthorityType.Aad)
            {
                return null;
            }

            Debug.Assert(homeAccountId != null);

            var idTokenCacheItems = GetAllIdTokensWithNoLocks(true, homeAccountId);

            ISet<string> allEnvironmentsInCache = new HashSet<string>(
                idTokenCacheItems.Select(aci => aci.Environment),
                StringComparer.OrdinalIgnoreCase);

            InstanceDiscoveryMetadataEntry instanceMetadata = await ServiceBundle.InstanceDiscoveryManager.GetMetadataEntryTryAvoidNetworkAsync(
                requestParameters.AuthorityInfo,
                allEnvironmentsInCache,
                requestParameters.RequestContext).ConfigureAwait(false);

            idTokenCacheItems = idTokenCacheItems.Where(idToken => instanceMetadata.Aliases.ContainsOrdinalIgnoreCase(idToken.Environment)).ToList();
            // some accessors might not support partitioning, so make sure to filter by home account id
            idTokenCacheItems = idTokenCacheItems.Where(idToken => homeAccountId.Equals(idToken.HomeAccountId)).ToList();

            Dictionary<string, TenantProfile> tenantProfiles = new Dictionary<string, TenantProfile>();
            foreach (MsalIdTokenCacheItem idTokenCacheItem in idTokenCacheItems)
            {
                tenantProfiles[idTokenCacheItem.TenantId] = new TenantProfile(idTokenCacheItem);
            }

            return tenantProfiles;
        }

        async Task ITokenCacheInternal.RemoveAccountAsync(IAccount account, AuthenticationRequestParameters requestParameters)
        {
            requestParameters.RequestContext.Logger.Verbose($"[RemoveAccountAsync] Entering token cache semaphore. Count {_semaphoreSlim.GetCurrentCountLogMessage()}");
            await _semaphoreSlim.WaitAsync(requestParameters.RequestContext.UserCancellationToken).ConfigureAwait(false);
            requestParameters.RequestContext.Logger.Verbose("[RemoveAccountAsync] Entered token cache semaphore");

            try
            {
                requestParameters.RequestContext.Logger.Info("Removing user from cache..");

                ITokenCacheInternal tokenCacheInternal = this;
                var partitionKey = account.HomeAccountId.Identifier;

                try
                {
                    if (tokenCacheInternal.IsExternalSerializationEnabled())
                    {
                        var args = new TokenCacheNotificationArgs(
                            this,
                            ClientId,
                            account,
                            true,
                            tokenCacheInternal.IsApplicationCache,
                            tokenCacheInternal.HasTokensNoLocks(partitionKey),
                            requestParameters.RequestContext.UserCancellationToken,
                            account.HomeAccountId.Identifier);

                        await tokenCacheInternal.OnBeforeAccessAsync(args).ConfigureAwait(false);
                        await tokenCacheInternal.OnBeforeWriteAsync(args).ConfigureAwait(false);
                    }

                    RemoveAccountInternal(account, requestParameters.RequestContext);
                    if (IsLegacyAdalCacheEnabled(requestParameters))
                    {
                        CacheFallbackOperations.RemoveAdalUser(
                           requestParameters.RequestContext.Logger,
                           LegacyCachePersistence,
                           ClientId,
                           account.Username,
                           account.HomeAccountId?.Identifier);
                    }
                }
                finally
                {
                    if (tokenCacheInternal.IsExternalSerializationEnabled())
                    {
                        var afterAccessArgs = new TokenCacheNotificationArgs(
                            this,
                            ClientId,
                            account,
                            true,
                            tokenCacheInternal.IsApplicationCache,
                            hasTokens: tokenCacheInternal.HasTokensNoLocks(partitionKey),
                            requestParameters.RequestContext.UserCancellationToken,
                            account.HomeAccountId.Identifier);

                        await tokenCacheInternal.OnAfterAccessAsync(afterAccessArgs).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
#pragma warning disable CS0618 // Type or member is obsolete
                HasStateChanged = false;
#pragma warning restore CS0618 // Type or member is obsolete

                _semaphoreSlim.Release();
            }
        }

        bool ITokenCacheInternal.HasTokensNoLocks(string partitionKey)
        {
            return _accessor.GetAllRefreshTokens(partitionKey).Count > 0 ||
                _accessor.GetAllAccessTokens(partitionKey).Any(at => !IsAtExpired(at));
        }

        private bool IsAtExpired(MsalAccessTokenCacheItem at)
        {
            return at.ExpiresOn < DateTime.UtcNow + AccessTokenExpirationBuffer;
        }

        internal /* internal for test only */ void RemoveAccountInternal(IAccount account, RequestContext requestContext)
        {
            if (account.HomeAccountId == null)
            {
                // adalv3 account
                return;
            }

            string partitionKey = account.HomeAccountId.Identifier;

            var allRefreshTokens = GetAllRefreshTokensWithNoLocks(false, partitionKey)
                .Where(item => item.HomeAccountId.Equals(account.HomeAccountId.Identifier, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // To maintain backward compatibility with other MSALs, filter all credentials by clientID if
            // Foci is disabled or if an FRT is not present
            bool filterByClientId = !_featureFlags.IsFociEnabled || !FrtExists(allRefreshTokens);

            // Delete all credentials associated with this IAccount
            var refreshTokensToDelete = filterByClientId ?
                allRefreshTokens.Where(x => x.ClientId.Equals(ClientId, StringComparison.OrdinalIgnoreCase)) :
                allRefreshTokens;

            foreach (MsalRefreshTokenCacheItem refreshTokenCacheItem in refreshTokensToDelete)
            {
                _accessor.DeleteRefreshToken(refreshTokenCacheItem.GetKey());
            }

            requestContext.Logger.Info("Deleted refresh token count - " + allRefreshTokens.Count);
            IList<MsalAccessTokenCacheItem> allAccessTokens = GetAllAccessTokensWithNoLocks(filterByClientId, partitionKey)
                .Where(item => item.HomeAccountId.Equals(account.HomeAccountId.Identifier, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (MsalAccessTokenCacheItem accessTokenCacheItem in allAccessTokens)
            {
                _accessor.DeleteAccessToken(accessTokenCacheItem.GetKey());
            }

            requestContext.Logger.Info("Deleted access token count - " + allAccessTokens.Count);

            var allIdTokens = GetAllIdTokensWithNoLocks(filterByClientId, partitionKey)
                .Where(item => item.HomeAccountId.Equals(account.HomeAccountId.Identifier, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (MsalIdTokenCacheItem idTokenCacheItem in allIdTokens)
            {
                _accessor.DeleteIdToken(idTokenCacheItem.GetKey());
            }

            requestContext.Logger.Info("Deleted Id token count - " + allIdTokens.Count);

            _accessor.GetAllAccounts(partitionKey)
                .Where(item => item.HomeAccountId.Equals(account.HomeAccountId.Identifier, StringComparison.OrdinalIgnoreCase) &&
                               item.PreferredUsername.Equals(account.Username, StringComparison.OrdinalIgnoreCase))
                .ToList()
                .ForEach(accItem => _accessor.DeleteAccount(accItem.GetKey()));
        }
    }
}
