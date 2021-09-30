﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Cache;
using Microsoft.Identity.Client.Core;
using Microsoft.Identity.Client.UI;
using Microsoft.Identity.Test.Common.Core.Mocks;
using Microsoft.Identity.Test.Common.Mocks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;

namespace Microsoft.Identity.Test.Unit
{
    [TestClass]
    public class AuthorityAliasesTests : TestBase
    {
        [TestMethod]
        [Description("Test authority migration")]
        public async Task AuthorityMigrationTestAsync()
        {
            // make sure that for all network calls "preferred_cache" environment is used
            // (it is taken from metadata in instance discovery response),
            // except very first network call - instance discovery

            using var harness = base.CreateTestHarness();

            var httpManager = harness.HttpManager;
            var authorityUri = new Uri(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "https://{0}/common",
                    TestConstants.ProductionNotPrefEnvironmentAlias));

            httpManager.AddInstanceDiscoveryMockHandler(authorityUri.AbsoluteUri);

            PublicClientApplication app = PublicClientApplicationBuilder.Create(TestConstants.ClientId)
                                                                        .WithAuthority(authorityUri, validateAuthority: true)
                                                                        .WithHttpManager(httpManager)
                                                                        .WithTelemetry(new TraceTelemetryConfig())
                                                                        .WithDebugLoggingCallback()
                                                                        .BuildConcrete();

            InMemoryTokenCache cache = new InMemoryTokenCache();
            cache.Bind(app.UserTokenCache);

            app.ServiceBundle.ConfigureMockWebUI(
                AuthorizationResult.FromUri(app.AppConfig.RedirectUri + "?code=some-code"),
                null,
                TestConstants.ProductionPrefNetworkEnvironment);

            // mock token request
            httpManager.AddMockHandler(new MockHttpMessageHandler
            {
                ExpectedUrl = string.Format(CultureInfo.InvariantCulture, "https://{0}/common/oauth2/v2.0/token",
                    TestConstants.ProductionPrefNetworkEnvironment),
                ExpectedMethod = HttpMethod.Post,
                ResponseMessage = MockHelpers.CreateSuccessTokenResponseMessage()
            });

            AuthenticationResult result = await app.AcquireTokenInteractive(TestConstants.s_scope).ExecuteAsync().ConfigureAwait(false);

            // make sure that all cache entities are stored with "preferred_cache" environment
            // (it is taken from metadata in instance discovery response)
            ValidateCacheEntitiesEnvironment(app.UserTokenCacheInternal, TestConstants.ProductionPrefCacheEnvironment);

            // silent request targeting at, should return at from cache for any environment alias
            foreach (var envAlias in TestConstants.s_prodEnvAliases)
            {
                var app2 = PublicClientApplicationBuilder.Create(TestConstants.ClientId)
                                                         .WithAuthority($"https://{envAlias}/common", validateAuthority: true)
                                                         .WithHttpManager(httpManager)
                                                         .WithTelemetry(new TraceTelemetryConfig())
                                                         .WithDebugLoggingCallback()
                                                         .BuildConcrete();

                cache.Bind(app2.UserTokenCache);

                IEnumerable<IAccount> accounts = await app.GetAccountsAsync().ConfigureAwait(false);
                result = await app2.AcquireTokenSilent(TestConstants.s_scope, accounts.First())
                                   .WithAuthority(string.Format(CultureInfo.InvariantCulture, "https://{0}/{1}/", envAlias, TestConstants.Utid))
                                   .WithForceRefresh(false)
                                   .ExecuteAsync(CancellationToken.None)
                                   .ConfigureAwait(false);

                Assert.IsNotNull(result);
            }

            // silent request targeting rt should find rt in cache for authority with any environment alias
            foreach (var envAlias in TestConstants.s_prodEnvAliases)
            {
                result = null;

                httpManager.AddMockHandler(
                    new MockHttpMessageHandler
                    {
                        ExpectedUrl = string.Format(CultureInfo.InvariantCulture, "https://{0}/{1}/oauth2/v2.0/token",
                            TestConstants.ProductionPrefNetworkEnvironment, TestConstants.Utid),
                        ExpectedMethod = HttpMethod.Post,
                        ExpectedPostData = new Dictionary<string, string>
                        {
                            { "grant_type", "refresh_token" }
                        },
                        // return not retriable status code
                        ResponseMessage = MockHelpers.CreateInvalidGrantTokenResponseMessage()
                    });

                try
                {
                    var app3 = PublicClientApplicationBuilder
                           .Create(TestConstants.ClientId)
                                 .WithAuthority($"https://{envAlias}/common", true)
                                 .WithHttpManager(httpManager)
                                 .WithTelemetry(new TraceTelemetryConfig())
                                 .WithDebugLoggingCallback()
                                 .BuildConcrete();

                    cache.Bind(app3.UserTokenCache);

                    result = await app3
                        .AcquireTokenSilent(
                            TestConstants.s_scopeForAnotherResource,
                            (await app.GetAccountsAsync().ConfigureAwait(false)).First())
                        .WithAuthority(string.Format(CultureInfo.InvariantCulture, "https://{0}/{1}/", envAlias, TestConstants.Utid))
                        .WithForceRefresh(false)
                        .ExecuteAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (MsalUiRequiredException)
                {
                }


                Assert.IsNull(result);
            }
        }

        [TestMethod]
        public void AuthorityNotIncludedInAliasesTestAsync()
        {
            //Make sure MSAL is able to create an entry for instance discovery when the configured environment is not present in the
            //instance discovery metadata. This is for non-public cloud scenarios. See https://github.com/AzureAD/microsoft-authentication-library-for-dotnet/issues/2701

            using var harness = base.CreateTestHarness();

            PublicClientApplication app = PublicClientApplicationBuilder.Create(TestConstants.ClientId)
                                                                        .WithAuthority(new Uri(TestConstants.AuthorityCommonPpeAuthority), true)
                                                                        .WithHttpManager(harness.HttpManager)
                                                                        .WithTelemetry(new TraceTelemetryConfig())
                                                                        .BuildConcrete();
            app.ServiceBundle.ConfigureMockWebUI();

            //Adding one instance discovery response to ensure the cache is hit for the subsiquent requests.
            //If MSAL tries to do an additional request this test will fail.
            harness.HttpManager.AddInstanceDiscoveryMockHandler();
            harness.HttpManager.AddSuccessTokenResponseMockHandlerForPost(TestConstants.AuthorityCommonPpeAuthority);

            AuthenticationResult result = app
                .AcquireTokenInteractive(TestConstants.s_scope)
                .ExecuteAsync(CancellationToken.None)
                .Result;

            Assert.IsNotNull(result);
        }

        private static void ValidateCacheEntitiesEnvironment(ITokenCacheInternal cache, string expectedEnvironment)
        {
            ICoreLogger logger = Substitute.For<ICoreLogger>();
            IEnumerable<Client.Cache.Items.MsalAccessTokenCacheItem> accessTokens = cache.Accessor.GetAllAccessTokens();
            foreach (Client.Cache.Items.MsalAccessTokenCacheItem at in accessTokens)
            {
                Assert.AreEqual(expectedEnvironment, at.Environment);
            }

            IEnumerable<Client.Cache.Items.MsalRefreshTokenCacheItem> refreshTokens = cache.Accessor.GetAllRefreshTokens();
            foreach (Client.Cache.Items.MsalRefreshTokenCacheItem rt in refreshTokens)
            {
                Assert.AreEqual(expectedEnvironment, rt.Environment);
            }

            IEnumerable<Client.Cache.Items.MsalIdTokenCacheItem> idTokens = cache.Accessor.GetAllIdTokens();
            foreach (Client.Cache.Items.MsalIdTokenCacheItem id in idTokens)
            {
                Assert.AreEqual(expectedEnvironment, id.Environment);
            }

            IEnumerable<Client.Cache.Items.MsalAccountCacheItem> accounts = cache.Accessor.GetAllAccounts();
            foreach (Client.Cache.Items.MsalAccountCacheItem account in accounts)
            {
                Assert.AreEqual(expectedEnvironment, account.Environment);
            }

            IDictionary<AdalTokenCacheKey, AdalResultWrapper> adalCache =
                AdalCacheOperations.Deserialize(logger, cache.LegacyPersistence.LoadCache());

            foreach (KeyValuePair<AdalTokenCacheKey, AdalResultWrapper> kvp in adalCache)
            {
                Assert.AreEqual(expectedEnvironment, new Uri(kvp.Key.Authority).Host);
            }
        }
    }
}
