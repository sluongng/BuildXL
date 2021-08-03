﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Distributed.NuCache.EventStreaming;
using BuildXL.Cache.ContentStore.Distributed.Redis;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;

#nullable enable

namespace BuildXL.Cache.ContentStore.Distributed.NuCache
{
    /// <summary>
    /// This is required in order to onboard new stamps to the <see cref="AzureBlobStorageCheckpointRegistry"/>. If we
    /// don't provide Redis as fallback, then no checkpoints will be found and we enter a weird state. Should be removed
    /// after we fully move to the new one.
    /// </summary>
    public class TransitioningCheckpointRegistry : ICheckpointRegistry
    {
        protected Tracer Tracer { get; } = new Tracer(nameof(TransitioningCheckpointRegistry));

        private readonly ICheckpointRegistry _primary;
        private readonly ICheckpointRegistry _fallback;

        public TransitioningCheckpointRegistry(
            ICheckpointRegistry primary,
            ICheckpointRegistry fallback)
        {
            _primary = primary;
            _fallback = fallback;
        }

        public async Task<Result<CheckpointState>> GetCheckpointStateAsync(OperationContext context)
        {
            var primaryResult = await _primary.GetCheckpointStateAsync(context);
            if (primaryResult.Succeeded)
            {
                return primaryResult.Value;
            }

            Tracer.Error(context, $"Failed to obtain checkpoint state from primary. Error=[{primaryResult}]");
            var fallbackResult = await _fallback.GetCheckpointStateAsync(context);
            if (fallbackResult.Succeeded)
            {
                return fallbackResult.Value;
            }

            return new Result<CheckpointState>(primaryResult & fallbackResult, "Failed to obtain checkpoint state from both primary and fallback");
        }

        public async Task<BoolResult> RegisterCheckpointAsync(OperationContext context, string checkpointId, EventSequencePoint sequencePoint)
        {
            var primaryResult = _primary.RegisterCheckpointAsync(context, checkpointId, sequencePoint);
            var fallbackResult = _fallback.GetCheckpointStateAsync(context);
            await Task.WhenAll(primaryResult, fallbackResult);
            return (await primaryResult) & (await fallbackResult);
        }
    }
}
