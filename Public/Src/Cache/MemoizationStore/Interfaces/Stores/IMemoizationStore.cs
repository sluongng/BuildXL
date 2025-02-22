// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Stores;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.MemoizationStore.Interfaces.Sessions;

namespace BuildXL.Cache.MemoizationStore.Interfaces.Stores
{
    /// <summary>
    ///     Standard interface for memoization stores.
    /// </summary>
    public interface IMemoizationStore : IStartupShutdown
    {
        /// <summary>
        ///     Create a new session that can add as well as read.
        /// </summary>
        /// <remarks>
        ///     As opposed to the method which takes a contentSession, this creates
        ///     an uncoupled MemoizationSession.  This currently means that it
        ///     will *automatically* overwrite entries on AddOrGets.
        /// </remarks>
        CreateSessionResult<IMemoizationSession> CreateSession(Context context, string name);

        /// <summary>
        ///     Create a new session that can add as well as read.
        /// </summary>
        /// <remarks>
        ///     This creates a memoization session that is coupled with the given content session.
        ///     This currently means that AddOrGets will automatically overwrite ContentHashLists
        ///     if any content is unavailable.
        /// </remarks>
        CreateSessionResult<IMemoizationSession> CreateSession(Context context, string name, IContentSession contentSession);

        /// <summary>
        ///     Gets a current stats snapshot.
        /// </summary>
        Task<GetStatsResult> GetStatsAsync(Context context);

        /// <summary>
        ///     Asynchronously enumerates the known strong fingerprints.
        /// </summary>
        IAsyncEnumerable<StructResult<StrongFingerprint>> EnumerateStrongFingerprints(Context context);
    }
}
