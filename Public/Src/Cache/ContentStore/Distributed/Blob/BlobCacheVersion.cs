﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#nullable enable

namespace BuildXL.Cache.ContentStore.Distributed.Blob;

/// <summary>
/// Version number to allow us to do backwards-incompatible changes.
/// </summary>
/// <remarks>
/// This is tightly coupled with <see cref="BlobCacheContainerName"/>
/// </remarks>
public enum BlobCacheVersion
{
    V0,
}
