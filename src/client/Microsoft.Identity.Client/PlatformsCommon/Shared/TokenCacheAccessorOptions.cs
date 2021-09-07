// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Identity.Client.PlatformsCommon.Shared
{
    internal class TokenCacheAccessorOptions
    {
        public static TokenCacheAccessorOptions CreateDefault()
        {
            return new TokenCacheAccessorOptions() { UseStatic = false };
        }

        public bool UseStatic { get; set; }

    }
}
