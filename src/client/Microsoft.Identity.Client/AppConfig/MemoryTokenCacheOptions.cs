// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.


/* Unmerged change from project 'Microsoft.Identity.Client (netcoreapp2.1)'
Before:
namespace Microsoft.Identity.Client.AppConfig
After:
using Microsoft;
using Microsoft.Identity;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.AppConfig
*/

namespace Microsoft.Identity.Client
{
    /// <summary>
    /// Options for the internal MSAL token caches
    /// </summary>
    public class MemoryTokenCacheOptions
    {
        /// <summary>
        /// Creates the default options
        /// </summary>
        public MemoryTokenCacheOptions() 
        {
            UseSharedCache = false;
        }

        /// <summary>
        /// Share the cache between all ClientApplication objects. The cache becomes static. Defaults to false.
        /// </summary>
        /// <remarks>ADAL used a static cache by default</remarks>
        public bool UseSharedCache { get; set; }

    }
}
