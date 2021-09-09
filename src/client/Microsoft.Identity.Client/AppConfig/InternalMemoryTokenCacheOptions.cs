// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Identity.Client
{
    /// <summary>
    /// Options for the internal MSAL token caches
    /// </summary>
    public class InternalMemoryTokenCacheOptions
    {
        /// <summary>
        /// Creates the default options
        /// </summary>
        public InternalMemoryTokenCacheOptions() 
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
