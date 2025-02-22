﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure.Storage.Blobs.Specialized;
using BuildXL.Cache.ContentStore.Distributed.Blob;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Secrets;
using BuildXL.Cache.ContentStore.Interfaces.Time;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.InterfacesTest;
using BuildXL.Cache.ContentStore.InterfacesTest.Results;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.Host.Configuration;
using BuildXL.Utilities.Core.Tracing;
using ContentStoreTest.Distributed.Redis;
using ContentStoreTest.Test;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

#nullable enable

namespace BuildXL.Cache.ContentStore.Distributed.Test.ContentLocation
{
    [Collection("Redis-based tests")]
    [Trait("Category", "WindowsOSOnly")] // 'redis-server' executable no longer exists
    public class BlobFolderStorageTests : TestWithOutput
    {
        private record TestAzureBlobStorageFolderConfiguration : AzureBlobStorageFolder.Configuration
        {
            public TestAzureBlobStorageFolderConfiguration(AzureStorageCredentials? credentials = null)
                : base(
                    Credentials: credentials ?? AzureStorageCredentials.StorageEmulator,
                    ContainerName: "blobfolderstoragetests",
                    // Use a random folder every time to avoid clashes
                    FolderName: Guid.NewGuid().ToString())
            {
            }
        }

        private readonly LocalRedisFixture _fixture;

        public BlobFolderStorageTests(LocalRedisFixture fixture, ITestOutputHelper output)
            : base(output)
        {
            _fixture = fixture;
        }

        /// <summary>
        /// This test is for a bug in Azurite (the Azure storage emulator)
        /// where creating a snapshot causes PutBlock operations with a lease to fail.
        /// </summary>
        [Fact(Skip = "The bug is still there, but we don't actually use snapshots.")]
        public async Task TestStorage()
        {
            using var storage = AzuriteStorageProcess.CreateAndStartEmpty(_fixture, TestGlobal.Logger);

            var creds = new AzureStorageCredentials(storage.ConnectionString);

            var client = creds.CreateCloudBlobClient();

            var container = client.GetContainerReference("test");

            await container.CreateIfNotExistsAsync();

            var blob = container.GetBlockBlobReference("test/sub/blob.out.bin");

            var bytes = Encoding.UTF8.GetBytes("hello");
            await blob.UploadFromByteArrayAsync(bytes, 0, bytes.Length);

            var leaseId = await blob.AcquireLeaseAsync(TimeSpan.FromSeconds(60));

            var snapshot = await blob.SnapshotAsync();

            await blob.PutBlockAsync("0000", new MemoryStream(), null, Microsoft.WindowsAzure.Storage.AccessCondition.GenerateLeaseCondition(leaseId), null, null);
        }

        [Fact]
        public Task CreatesMissingContainerOnWrite()
        {
            return RunTest(
                async (context, store, adapter, clock) =>
                {
                    var file = new BlobPath("ThisIsATest", relative: true);
                    var client = store.GetBlobClient(file);
                    await adapter.WriteAsync(context, client, "Test").ShouldBeSuccess();

                    var r = await adapter.EnsureContainerExists(context, client.GetParentBlobContainerClient()).ShouldBeSuccess();
                    r.Value.Should().BeFalse();
                },
                elideStartup: true);
        }

        [Fact]
        public Task DoesNotCreateMissingContainerOnRead()
        {
            return RunTest(
                async (context, store, adapter, clock) =>
                {
                    var file = new BlobPath("ThisIsATest", relative: true);
                    var client = store.GetBlobClient(file);
                    var r = await adapter.ReadStateAsync<string>(context, client).ShouldBeSuccess();
                    r.Value!.Value.Should().BeNull();

                    var cr = await adapter.EnsureContainerExists(context, client.GetParentBlobContainerClient()).ShouldBeSuccess();
                    cr.Value.Should().BeTrue();
                },
                elideStartup: true);
        }

        [Theory(
            Skip = "This is used for manual verification, because running these tests takes long, is random, and we don't want to have a flaky test")]
        [InlineData(10, 10, 60)]
        [InlineData(1024, 1, 180)] // Usually ~1.5m
        [InlineData(2048, 1, 1000)] // Usually ~3m
        public Task ConcurrentReadModifyWriteEventuallyFinishes(int numTasks, int numIncrementsPerTask, double maxDurationSeconds)
        {
            var maxTestDuration = TimeSpan.FromSeconds(maxDurationSeconds);

            return RunTest(
                async (context, store, adapter, clock) =>
                {
                    var path = new BlobPath("race.json", relative: true);
                    var blob = store.GetBlobClient(path);

                    var started = 0;
                    var startSemaphore = new SemaphoreSlim(0, numTasks + 1);

                    await adapter.WriteAsync<int>(context, blob, 0).ShouldBeSuccess();

                    var tasks = new Task[numTasks];
                    for (var i = 0; i < numTasks; i++)
                    {
                        tasks[i] = Task.Run(
                            async () =>
                            {
                                Interlocked.Increment(ref started);
                                await startSemaphore.WaitAsync();

                                for (var j = 0; j < numIncrementsPerTask; j++)
                                {
                                    await adapter.ReadModifyWriteAsync<int, int>(context, blob, state => (state, state + 1)).ShouldBeSuccess();
                                }
                            });
                    }

                    // Wait until they all start, and release them all at once
                    while (started < numTasks)
                    {
                        await Task.Delay(1);
                    }

                    // Perform experiment
                    var stopwatch = StopwatchSlim.Start();
                    startSemaphore.Release(numTasks);
                    await Task.WhenAll(tasks);
                    var elapsed = stopwatch.Elapsed;

                    // Ensure value is what we expected it to be
                    var r = await adapter.ReadAsync<int>(context, blob).ShouldBeSuccess();
                    r.Value.Should().Be(numTasks * numIncrementsPerTask);

                    // Ensure time taken is what we expected it to be
                    elapsed.Should().BeLessOrEqualTo(maxTestDuration);
                },
                timeout: maxTestDuration,
                configuration: new BlobFolderStorageConfiguration()
                {
                    RetryPolicy = new RetryPolicyConfiguration()
                    {
                        RetryPolicy = StandardRetryPolicy.ExponentialSpread,
                        MinimumRetryWindow = TimeSpan.FromMilliseconds(1),
                        MaximumRetryWindow = TimeSpan.FromSeconds(5),
                        WindowJitter = 1.0,
                    },
                });
        }

        private Task RunTest(
            Func<OperationContext, AzureBlobStorageFolder, BlobStorageClientAdapter, IClock, Task> runTest,
            IClock? clock = null,
            TestAzureBlobStorageFolderConfiguration? folderConfiguration = null,
            BlobFolderStorageConfiguration? configuration = null,
            bool elideStartup = false,
            TimeSpan? timeout = null,
            string? connectionString = null,
            [CallerMemberName] string? caller = null)
        {
            clock ??= SystemClock.Instance;
            timeout ??= Timeout.InfiniteTimeSpan;

            var tracer = new Tracer(caller ?? nameof(BlobFolderStorageTests));
            var tracingContext = new Context(TestGlobal.Logger);
            var context = new OperationContext(tracingContext);

            // This is here just so we display the text run duration in the logs
            return context.PerformOperationWithTimeoutAsync(
                tracer,
                async context =>
                {
                    using var storage = AzuriteStorageProcess.CreateAndStartEmpty(_fixture, TestGlobal.Logger);
                    connectionString ??= storage.ConnectionString;

                    folderConfiguration ??= new TestAzureBlobStorageFolderConfiguration(new AzureStorageCredentials(connectionString));
                    var blobFolderStorage = new BlobStorageClientAdapter(tracer, configuration ?? new BlobFolderStorageConfiguration());

                    await runTest(context, folderConfiguration.Create(), blobFolderStorage, clock);

                    return BoolResult.Success;
                },
                timeout: timeout.Value,
                traceOperationStarted: false,
                caller: caller ?? nameof(BlobFolderStorageTests)).ThrowIfFailureAsync();
        }
    }
}
