// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.ContentStore.Utils;
using BuildXL.Cache.Host.Configuration;
using BuildXL.Utilities.Core.Tasks;
using Microsoft.Azure.EventHubs;
using Microsoft.Azure.Services.AppAuthentication;

namespace BuildXL.Cache.ContentStore.Distributed.NuCache.EventStreaming
{
    /// <summary>
    /// An event hub client which interacts with Azure Event Hub service
    /// </summary>
    public class AzureEventHubClient : StartupShutdownSlimBase, IEventHubClient
    {
        private const string PartitionId = "0";

        private EventHubClient _eventHubClient;
        private PartitionSender _partitionSender;
        private readonly EventHubContentLocationEventStoreConfiguration _configuration;

        private readonly string _hostName = Guid.NewGuid().ToString();

        private PartitionReceiver _partitionReceiver;

        /// <inheritdoc />
        protected override Tracer Tracer { get; } = new Tracer(nameof(AzureEventHubClient));

        /// <nodoc />
        public AzureEventHubClient(EventHubContentLocationEventStoreConfiguration configuration)
        {
            _configuration = configuration;
        }

        /// <inheritdoc />
        public BoolResult StartProcessing(OperationContext context, EventSequencePoint sequencePoint, IPartitionReceiveHandler processor)
        {
            Tracer.Info(context, $"{Tracer.Name}: Initializing event processing for event hub '{_configuration.EventHubName}' and consumer group '{_configuration.ConsumerGroupName}'.");

            if (_partitionReceiver == null)
            {
                _partitionReceiver = _eventHubClient.CreateReceiver(
                    _configuration.ConsumerGroupName,
                    PartitionId,
                    GetInitialOffset(context, sequencePoint),
                    new ReceiverOptions()
                    {
                        Identifier = _hostName
                    });

                _partitionReceiver.SetReceiveHandler(processor);
            }

            return BoolResult.Success;
        }

        /// <inheritdoc />
        public BoolResult SuspendProcessing(OperationContext context)
        {
            // In unit tests, hangs sometimes occur for this when running multiple tests in sequence.
            // Adding a timeout to detect when this occurs
            if (_partitionReceiver != null)
            {
                _partitionReceiver.CloseAsync().WithTimeoutAsync(TimeSpan.FromMinutes(1)).GetAwaiter().GetResult();
                _partitionReceiver = null;
            }

            return BoolResult.Success;
        }

        /// <inheritdoc />
        protected override async Task<BoolResult> StartupCoreAsync(OperationContext context)
        {
            await base.StartupCoreAsync(context).ThrowIfFailure();

            // Retry behavior in the Azure Event Hubs Client Library is controlled by the RetryPolicy property on the EventHubClient class.
            // The default policy retries with exponential backoff when Azure Event Hub returns a transient EventHubsException or an OperationCanceledException.
            if (ManagedIdentityUriHelper.TryParseForManagedIdentity(_configuration.EventHubConnectionString, out Uri eventHubNamespaceUri, out string eventHubName, out string managedIdentityId))
            {
                // https://docs.microsoft.com/en-us/dotnet/api/overview/azure/service-to-service-authentication#connection-string-support
                var tokenProvider = new ManagedIdentityTokenProvider(new AzureServiceTokenProvider($"RunAs=App;AppId={managedIdentityId}"));
                _eventHubClient = EventHubClient.CreateWithTokenProvider(
                    eventHubNamespaceUri,
                    eventHubName,
                    tokenProvider);
            }
            else
            {
                var connectionStringBuilder =
                    new EventHubsConnectionStringBuilder(_configuration.EventHubConnectionString)
                    {
                        EntityPath = _configuration.EventHubName,
                    };

                _eventHubClient = EventHubClient.CreateFromConnectionString(connectionStringBuilder.ToString());
            }

            _partitionSender = _eventHubClient.CreatePartitionSender(PartitionId);

            return BoolResult.Success;
        }

        /// <inheritdoc />
        protected override async Task<BoolResult> ShutdownCoreAsync(OperationContext context)
        {
            SuspendProcessing(context).ThrowIfFailure();

            if (_partitionSender != null)
            {
                await _partitionSender.CloseAsync();
            }

            if (_eventHubClient != null)
            {
                await _eventHubClient.CloseAsync();
            }

            return await base.ShutdownCoreAsync(context);
        }

        /// <inheritdoc />
        public async Task SendAsync(OperationContext context, EventData eventData)
        {
            context.Token.ThrowIfCancellationRequested();
            try
            {
                await _partitionSender.SendAsync(eventData);
            }
            catch (InvalidOperationException) when(context.Token.IsCancellationRequested || ShutdownStarted)
            {
                // We started shutting down the instance. The operation may fail in this case.
                // Don't re-throw any errors. All the state changes that were not delivered would be resent during reconciliation process.
            }
        }

        private EventPosition GetInitialOffset(OperationContext context, EventSequencePoint sequencePoint)
        {
            Tracer.Debug(context, $"Consuming events from '{sequencePoint}'.");
            return sequencePoint.EventPosition;
        }
    }
}
