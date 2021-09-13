using System;
using System.Linq;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.ApiConfig.Parameters;
using Microsoft.Identity.Client.Cache;
using Microsoft.Identity.Client.Cache.Items;
using Microsoft.Identity.Client.Instance;
using Microsoft.Identity.Client.Internal;
using Microsoft.Identity.Client.Internal.Requests;
using Microsoft.Identity.Client.TelemetryCore.Internal.Events;
using Microsoft.Identity.Test.Common;
using Microsoft.Identity.Test.Common.Core.Mocks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.Identity.Test.Unit.CacheTests
{
    [TestClass]
    public class SuggestedWebCacheKeyTests
    {
        private IServiceBundle _serviceBundle;

        [TestInitialize]
        public void TestInitialize()
        {
            _serviceBundle = TestCommon.CreateDefaultServiceBundle();
        }

        [TestMethod]
        public void TestCacheKeyForADFSAuthority()
        {
            // Arrange
            var appTokenCache = new TokenCache(_serviceBundle, isApplicationTokenCache: true);
            var requestContext = new RequestContext(_serviceBundle , Guid.NewGuid());
            var authority = Authority.CreateAuthority(TestConstants.ADFSAuthority, true);

            requestContext.ServiceBundle.Config.AuthorityInfo = authority.AuthorityInfo;

            var acquireTokenCommonParameters = new AcquireTokenCommonParameters
            {
                ApiId = ApiEvent.ApiIds.AcquireTokenForClient,                
            };

            var parameters = new AuthenticationRequestParameters(
                _serviceBundle,
                appTokenCache,
                acquireTokenCommonParameters, 
                requestContext,
                authority);


            // Act
            var actualKey = SuggestedWebCacheKeyFactory.GetKeyFromRequest(parameters);

            // Assert
            Assert.IsNotNull(actualKey);
            var expectedKey = $"{_serviceBundle.Config.ClientId}__AppTokenCache";
            Assert.AreEqual(expectedKey, actualKey);
        }

        [TestMethod]
        public void TestCacheKeyForTenantAuthority()
        {
            // Arrange
            var appTokenCache = new TokenCache(_serviceBundle, isApplicationTokenCache: true);
            var requestContext = new RequestContext(_serviceBundle , Guid.NewGuid());
            var tenantAuthority = AuthorityInfo.FromAadAuthority(AzureCloudInstance.AzurePublic, tenant: TestConstants.AadTenantId, validateAuthority: false);
            var acquireTokenCommonParameters = new AcquireTokenCommonParameters
            {
                ApiId = ApiEvent.ApiIds.AcquireTokenForClient,
                AuthorityOverride = tenantAuthority
            };

            var parameters = new AuthenticationRequestParameters(
                _serviceBundle,
                appTokenCache,
                acquireTokenCommonParameters, 
                requestContext, 
                Authority.CreateAuthority(tenantAuthority));


            // Act
            var actualKey = SuggestedWebCacheKeyFactory.GetKeyFromRequest(parameters);

            // Assert
            Assert.IsNotNull(actualKey);
            var expectedKey = $"{_serviceBundle.Config.ClientId}_{TestConstants.AadTenantId}_AppTokenCache";
            Assert.AreEqual(expectedKey, actualKey);
        }

        [TestMethod]
        public void TestCacheKeyForRemoveAccount()
        {
            // Arrange
            var appTokenCache = new TokenCache(_serviceBundle, isApplicationTokenCache: true);
            var requestContext = new RequestContext(_serviceBundle, Guid.NewGuid());
            var tenantAuthority = AuthorityInfo.FromAadAuthority(AzureCloudInstance.AzurePublic, tenant: TestConstants.AadTenantId, validateAuthority: false);
            var acquireTokenCommonParameters = new AcquireTokenCommonParameters
            {
                ApiId = ApiEvent.ApiIds.RemoveAccount,
                AuthorityOverride = tenantAuthority
            };

            var parameters = new AuthenticationRequestParameters(
                _serviceBundle,
                appTokenCache,
                acquireTokenCommonParameters,
                requestContext,
                Authority.CreateAuthority(tenantAuthority),
                TestConstants.HomeAccountId)
            {
                Account = new Account(TestConstants.HomeAccountId, TestConstants.Username, TestConstants.ProductionPrefCacheEnvironment)
            };

            // Act
            var actualKey = SuggestedWebCacheKeyFactory.GetKeyFromRequest(parameters);

            // Assert
            Assert.IsNotNull(actualKey);
            Assert.AreEqual(parameters.HomeAccountId, actualKey);
        }

        [TestMethod]
        public void PartitionKeyForCache()
        {
            var cache = new TokenCache(_serviceBundle, isApplicationTokenCache: false) ;
            var accessor = (cache as ITokenCacheInternal).Accessor;
            TokenCacheHelper.PopulateCache((cache as ITokenCacheInternal).Accessor);

            var at = accessor.GetAllAccessTokens().First();
            var rt = accessor.GetAllRefreshTokens().First();
            var idt = accessor.GetAllIdTokens().First();
            var acc = accessor.GetAllAccounts().First();

            Assert.AreEqual(at.HomeAccountId, SuggestedWebCacheKeyFactory.GetKeyFromCachedItem(at));
            Assert.AreEqual(rt.HomeAccountId, SuggestedWebCacheKeyFactory.GetKeyFromCachedItem(rt));
            Assert.AreEqual(idt.HomeAccountId, SuggestedWebCacheKeyFactory.GetKeyFromCachedItem(idt));
            Assert.AreEqual(acc.HomeAccountId, SuggestedWebCacheKeyFactory.GetKeyFromCachedItem(acc));

            at.UserAssertionHash = "at_hash";
            rt.UserAssertionHash = "rt_hash";
            Assert.AreEqual("at_hash", SuggestedWebCacheKeyFactory.GetKeyFromCachedItem(at));
            Assert.AreEqual("rt_hash", SuggestedWebCacheKeyFactory.GetKeyFromCachedItem(rt));
            Assert.AreEqual(idt.HomeAccountId, SuggestedWebCacheKeyFactory.GetKeyFromCachedItem(idt));
            Assert.AreEqual(acc.HomeAccountId, SuggestedWebCacheKeyFactory.GetKeyFromCachedItem(acc));
        }
    }
}
