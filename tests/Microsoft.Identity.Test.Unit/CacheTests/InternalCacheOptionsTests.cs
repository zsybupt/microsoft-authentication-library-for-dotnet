// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using Microsoft.Identity.Test.Common.Core.Helpers;
using Microsoft.Identity.Test.Common.Core.Mocks;
using Microsoft.Identity.Test.Common.Mocks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.Identity.Test.Unit.CacheTests
{
    [TestClass]
    public class InternalCacheOptionsTests : TestBase
    {
        [TestMethod]
        public void OptionsAndExternalCacheAreExclusiveAsync()
        {
            var app =
                    ConfidentialClientApplicationBuilder.Create(TestConstants.ClientId)
                                                              .WithClientSecret(TestConstants.ClientSecret)
                                                              .WithInternalMemoryTokenCacheOptions(new InternalMemoryTokenCacheOptions() { UseSharedCache = true })
                                                              .Build();

            var ex = AssertException.Throws<MsalClientException>(() => app.UserTokenCache.SetAfterAccess((n) => { }));
            Assert.AreEqual(MsalError.ExternalInternalCacheSerialization, ex.ErrorCode);
            ex = AssertException.Throws<MsalClientException>(() => app.UserTokenCache.SetBeforeAccess((n) => { }));
            Assert.AreEqual(MsalError.ExternalInternalCacheSerialization, ex.ErrorCode);
            ex = AssertException.Throws<MsalClientException>(() => app.UserTokenCache.SetBeforeWrite((n) => { }));
            Assert.AreEqual(MsalError.ExternalInternalCacheSerialization, ex.ErrorCode);

            ex = AssertException.Throws<MsalClientException>(() => app.UserTokenCache.SetBeforeAccessAsync((n) => Task.CompletedTask));
            Assert.AreEqual(MsalError.ExternalInternalCacheSerialization, ex.ErrorCode);
            ex = AssertException.Throws<MsalClientException>(() => app.UserTokenCache.SetAfterAccessAsync((n) => Task.CompletedTask));
            Assert.AreEqual(MsalError.ExternalInternalCacheSerialization, ex.ErrorCode);
            ex = AssertException.Throws<MsalClientException>(() => app.UserTokenCache.SetBeforeWriteAsync((n) => Task.CompletedTask));
            Assert.AreEqual(MsalError.ExternalInternalCacheSerialization, ex.ErrorCode);
        }

        [TestMethod]
        public async Task ClientCreds_StaticCache_Async()
        {
            using (var httpManager = new MockHttpManager())
            {
                httpManager.AddInstanceDiscoveryMockHandler();

                ConfidentialClientApplication app1 =
                    ConfidentialClientApplicationBuilder.Create(TestConstants.ClientId)
                                                              .WithClientSecret(TestConstants.ClientSecret)
                                                              .WithHttpManager(httpManager)
                                                              .WithInternalMemoryTokenCacheOptions(new InternalMemoryTokenCacheOptions() { UseSharedCache = true }).BuildConcrete();

                ConfidentialClientApplication app2 =
                   ConfidentialClientApplicationBuilder.Create(TestConstants.ClientId)
                                                             .WithClientSecret(TestConstants.ClientSecret)
                                                             .WithHttpManager(httpManager)
                                                              .WithInternalMemoryTokenCacheOptions(new InternalMemoryTokenCacheOptions() { UseSharedCache = true }).BuildConcrete();

                ConfidentialClientApplication app_withoutStaticCache =
                  ConfidentialClientApplicationBuilder.Create(TestConstants.ClientId)
                                                            .WithClientSecret(TestConstants.ClientSecret)
                                                            .WithHttpManager(httpManager)
                                                            .BuildConcrete();

                httpManager.AddMockHandlerSuccessfulClientCredentialTokenResponseMessage();
                await ClientCredsAssertTokenSourceAsync(app1, "S1", TokenSource.IdentityProvider).ConfigureAwait(false);

                httpManager.AddMockHandlerSuccessfulClientCredentialTokenResponseMessage();
                await ClientCredsAssertTokenSourceAsync(app1, "S2", TokenSource.IdentityProvider).ConfigureAwait(false);


                await ClientCredsAssertTokenSourceAsync(app2, "S1", TokenSource.Cache).ConfigureAwait(false);
                await ClientCredsAssertTokenSourceAsync(app2, "S2", TokenSource.Cache).ConfigureAwait(false);

                ConfidentialClientApplication app3 =
                     ConfidentialClientApplicationBuilder.Create(TestConstants.ClientId)
                                                               .WithClientSecret(TestConstants.ClientSecret)
                                                               .WithHttpManager(httpManager)
                                                              .WithInternalMemoryTokenCacheOptions(new InternalMemoryTokenCacheOptions() { UseSharedCache = true }).BuildConcrete();

                await ClientCredsAssertTokenSourceAsync(app3, "S1", TokenSource.Cache).ConfigureAwait(false);
                await ClientCredsAssertTokenSourceAsync(app3, "S2", TokenSource.Cache).ConfigureAwait(false);

                httpManager.AddMockHandlerSuccessfulClientCredentialTokenResponseMessage();
                await ClientCredsAssertTokenSourceAsync(app_withoutStaticCache, "S1", TokenSource.IdentityProvider).ConfigureAwait(false);

                httpManager.AddMockHandlerSuccessfulClientCredentialTokenResponseMessage();
                await ClientCredsAssertTokenSourceAsync(app_withoutStaticCache, "S2", TokenSource.IdentityProvider).ConfigureAwait(false);

            }
        }

        [TestMethod]
        public async Task PublicClient_StaticCache_Async()
        {
            using (var harness = CreateTestHarness())
            {
                harness.HttpManager.AddInstanceDiscoveryMockHandler();

                var app1 = PublicClientApplicationBuilder
                    .Create(TestConstants.ClientId)
                    .WithAuthority(new Uri(ClientApplicationBase.DefaultAuthority), true)
                    .WithHttpManager(harness.HttpManager)
                    .WithInternalMemoryTokenCacheOptions(new InternalMemoryTokenCacheOptions() { UseSharedCache = true }).BuildConcrete();

                app1.ServiceBundle.ConfigureMockWebUI();

                harness.HttpManager.AddSuccessTokenResponseMockHandlerForPost(TestConstants.AuthorityCommonTenant);

                AuthenticationResult result = app1
                    .AcquireTokenInteractive(TestConstants.s_scope)                
                    .ExecuteAsync()
                    .Result;


                var accounts = await app1.GetAccountsAsync().ConfigureAwait(false);
                Assert.AreEqual(1, accounts.Count());
                result = await app1.AcquireTokenSilent(TestConstants.s_scope, accounts.Single()).ExecuteAsync().ConfigureAwait(false);

                var app2 = PublicClientApplicationBuilder
                   .Create(TestConstants.ClientId)
                   .WithAuthority(new Uri(ClientApplicationBase.DefaultAuthority), true)
                   .WithHttpManager(harness.HttpManager)
                   .WithInternalMemoryTokenCacheOptions(new InternalMemoryTokenCacheOptions() { UseSharedCache = true }).BuildConcrete();

                accounts = await app2.GetAccountsAsync().ConfigureAwait(false);
                Assert.AreEqual(1, accounts.Count());
                result = await app2.AcquireTokenSilent(TestConstants.s_scope, accounts.Single()).ExecuteAsync().ConfigureAwait(false);
            }
        }



        private async Task ClientCredsAssertTokenSourceAsync(IConfidentialClientApplication app, string scope, TokenSource expectedSource)
        {
            var result = await app.AcquireTokenForClient(new[] { scope })
                 .WithAuthority(TestConstants.AuthorityUtidTenant)
                 .ExecuteAsync().ConfigureAwait(false);
            Assert.AreEqual(
               expectedSource,
               result.AuthenticationResultMetadata.TokenSource);
        }
    }
}
