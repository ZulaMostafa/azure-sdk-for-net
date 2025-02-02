﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Core.Diagnostics;
using Azure.Messaging.EventHubs.Amqp;
using Azure.Messaging.EventHubs.Core;
using Azure.Messaging.EventHubs.Diagnostics;

namespace Azure.Messaging.EventHubs.Producer
{
    /// <summary>
    ///   A client responsible for publishing instances of <see cref="EventData"/> to a specific
    ///   Event Hub.  Depending on the options specified when events are enqueued, they may be
    ///   automatically assigned to a partition, grouped according to the specified partition
    ///   key, or assigned a specifically requested partition.
    ///
    ///   The <see cref="EventHubBufferedProducerClient" /> does not publish immediately, instead using
    ///   a deferred model where events are collected into a buffer so that they may be efficiently batched
    ///   and published when the batch is full or the <see cref="EventHubBufferedProducerClientOptions.MaximumWaitTime" />
    ///   has elapsed with no new events enqueued.
    ///
    ///   This model is intended to shift the burden of batch management from callers, at the cost of non-deterministic
    ///   timing, for when events will be published. There are additional trade-offs to consider, as well:
    ///   <list type="bullet">
    ///     <item><description>If the application crashes, events in the buffer will not have been published.  To prevent data loss, callers are encouraged to track publishing progress using the <see cref="SendEventBatchSucceededAsync" /> and <see cref="SendEventBatchFailedAsync" /> handlers.</description></item>
    ///     <item><description>Events specifying a partition key may be assigned a different partition than those using the same key with other producers.</description></item>
    ///     <item><description>In the unlikely event that a partition becomes temporarily unavailable, the <see cref="EventHubBufferedProducerClient" /> may take longer to recover than other producers.</description></item>
    ///   </list>
    ///
    ///   In scenarios where it is important to have events published immediately with a deterministic outcome, ensure
    ///   that partition keys are assigned to a partition consistent with other publishers, or where maximizing availability
    ///   is a requirement, using the <see cref="EventHubProducerClient" /> is recommended.
    /// </summary>
    ///
    /// <remarks>
    ///   The <see cref="EventHubBufferedProducerClient"/> is safe to cache and use as a singleton for the lifetime of an
    ///   application. This is the recommended approach, since the client is responsible for efficient network,
    ///   CPU, and memory use. Calling <see cref="CloseAsync(bool, CancellationToken)"/> or <see cref="DisposeAsync"/>
    ///   is required so that resources can be cleaned up after use.
    /// </remarks>
    ///
    /// <seealso cref="EventHubProducerClient" />
    ///
    internal class EventHubBufferedProducerClient : IAsyncDisposable
    {
        /// <summary>The maximum amount of time, in milliseconds, to allow for acquiring the semaphore guarding a partition's publishing eligibility.</summary>
        private const int PartitionPublishingGuardAcquireLimitMilliseconds = 100;

        /// <summary>The minimum interval to allow for waiting when building a batch to publish.</summary>
        private static readonly TimeSpan MinimumPublishingWaitInterval = TimeSpan.FromMilliseconds(5);

        /// <summary>The default interval to delay for events to be available when building a batch to publish.</summary>
        private static readonly TimeSpan DefaultPublishingDelayInterval = TimeSpan.FromMilliseconds(25);

        /// <summary>
        ///   The set of client options to use when options were not passed when the producer was instantiated.
        /// </summary>
        ///
        private static EventHubBufferedProducerClientOptions DefaultOptions { get; } =
            new EventHubBufferedProducerClientOptions
            {
                RetryOptions = new EventHubsRetryOptions { MaximumRetries = 15, TryTimeout = TimeSpan.FromMinutes(3) }
            };

        /// <summary>The set of currently active partition publishing tasks.  Partition identifiers are used as keys.</summary>
        private readonly ConcurrentDictionary<string, PartitionPublishingState> _activePartitionPublishingStateMap = new();

        /// <summary>The set of options to use with the <see cref="EventHubBufferedProducerClient" /> instance.</summary>
        private readonly EventHubBufferedProducerClientOptions _options;

        /// <summary>The primitive for synchronizing access when class-wide state is changing.</summary>
        [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "The AvailableWaitHandle property is not accessed; resources requiring dispose will not have been allocated.")]
        private readonly SemaphoreSlim _stateGuard = new SemaphoreSlim(1, 1);

        /// <summary>The producer to use to send events to the Event Hub.</summary>
        [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "It is being disposed via delegation to CloseAsync.")]
        private readonly EventHubProducerClient _producer;

        /// <summary>A <see cref="CancellationTokenSource"/> instance to signal the request to cancel the background tasks responsible for publishing and management after any in-process batches are complete.</summary>
        [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "It is being disposed via delegation to StopPublishingAsync, which is called by CloseAsync.")]
        private CancellationTokenSource _backgroundTasksCancellationSource;

        /// <summary>A <see cref="CancellationTokenSource"/> instance to signal that any active publishing operations, including those in-flight, should terminate immediately.</summary>
        private CancellationTokenSource _activeSendOperationsCancellationSource;

        /// <summary>The task responsible for managing the operations of the producer when it is running.</summary>
        private Task _producerManagementTask;

        /// <summary>The task responsible for publishing events.</summary>
        private Task _publishingTask;

        /// <summary>The set of partitions identifiers for the configured Event Hub, intended to be used for partition assignment.</summary>
        private string[] _partitions;

        /// <summary>A hash representing the set of partitions identifiers for the configured Event Hub, intended to be used for partition validation.</summary>
        private HashSet<string> _partitionHash;

        /// <summary>The count of total events that have been buffered across all partitions.</summary>
        private int _totalBufferedEventCount;

        /// <summary>The handler to be called once a batch has successfully published.</summary>
        private Func<SendEventBatchSucceededEventArgs, Task> _sendSucceededHandler;

        /// <summary>The handler to be called once a batch has failed to publish.</summary>
        private Func<SendEventBatchFailedEventArgs, Task> _sendFailedHandler;

        /// <summary>Indicates whether or not registration for handlers is locked; if so, no changes are permitted.</summary>
        private volatile bool _areHandlersLocked;

        /// <summary>Indicates whether or not this instance has been closed.</summary>
        private volatile bool _isClosed;

        /// <summary>
        ///   The fully qualified Event Hubs namespace that this producer is currently associated with, which will likely be similar
        ///   to <c>{yournamespace}.servicebus.windows.net</c>.
        /// </summary>
        ///
        public string FullyQualifiedNamespace => _producer.FullyQualifiedNamespace;

        /// <summary>
        ///   The name of the Event Hub that this producer is connected to, specific to the Event Hubs namespace that contains it.
        /// </summary>
        ///
        public string EventHubName => _producer.EventHubName;

        /// <summary>
        ///   A unique name to identify the buffered producer.
        /// </summary>
        ///
        public string Identifier => _producer.Identifier;

        /// <summary>
        ///   Indicates whether or not this <see cref="EventHubBufferedProducerClient" /> is currently
        ///   active and publishing queued events.
        /// </summary>
        ///
        /// <value>
        ///   <c>true</c> if the client is publishing; otherwise, <c>false</c>.
        /// </value>
        ///
        /// <remarks>
        ///   The producer will begin publishing when an event is enqueued and should remain active until
        ///   either <see cref="CloseAsync" /> or <see cref="DisposeAsync" /> is called.
        ///
        ///   If any events were enqueued, <see cref="IsClosed" /> is <c>false</c>, and <see cref="IsPublishing" />
        ///   is <c>false</c>, this likely indicates an unrecoverable state for the client.  It is recommended to
        ///   close the <see cref="EventHubBufferedProducerClient" /> and create a new instance.
        ///
        ///   In this state, exceptions will be reported by the Event Hubs client library logs, which can be captured
        ///   using the <see cref="Azure.Core.Diagnostics.AzureEventSourceListener" />.
        /// </remarks>
        ///
        /// <seealso href="https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/eventhub/Azure.Messaging.EventHubs/samples/Sample10_AzureEventSourceListener.md">Capturing Event Hubs logs</seealso>
        ///
        public virtual bool IsPublishing => _producerManagementTask != null;

        /// <summary>
        ///   Indicates whether or not this <see cref="EventHubBufferedProducerClient" /> has been closed.
        /// </summary>
        ///
        /// <value>
        ///   <c>true</c> if the client is closed; otherwise, <c>false</c>.
        /// </value>
        ///
        public virtual bool IsClosed
        {
            get => _isClosed;
            protected internal set => _isClosed = value;
        }

        /// <summary>
        ///   The total number of events that are currently buffered and waiting to be published, across all partitions.
        /// </summary>
        ///
        public virtual int TotalBufferedEventCount => _totalBufferedEventCount;

        /// <summary>
        ///   The instance of <see cref="EventHubsEventSource" /> which can be mocked for testing.
        /// </summary>
        ///
        /// <remarks>
        ///   This member is exposed internally to support testing only; it is not intended
        ///   for other use.
        /// </remarks>
        ///
        internal EventHubsEventSource Logger { get; set; } = EventHubsEventSource.Log;

        /// <summary>
        ///   The interval at which the background management operations should run.
        /// </summary>
        ///
        /// <remarks>
        ///   This member is exposed internally to support testing only; it is not intended
        ///   for other use.
        /// </remarks>
        ///
        internal TimeSpan BackgroundManagementInterval { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        ///   The set of state for the partitions which are actively being published to.  Partition identifiers are used as keys.
        /// </summary>
        ///
        /// <remarks>
        ///   This member is exposed internally to support testing only; it is not intended
        ///   for other use.
        /// </remarks>
        ///
        internal ConcurrentDictionary<string, PartitionPublishingState> ActivePartitionPublishingState => _activePartitionPublishingStateMap;

        /// <summary>
        ///    Invoked after each batch of events has been successfully published to the Event Hub, this
        ///    handler is optional and is intended to provide notifications for interested listeners.
        ///
        ///   It is not recommended to invoke <see cref="CloseAsync" /> or <see cref="DisposeAsync" /> from this handler; doing so may result
        ///   in a deadlock scenario if those calls are awaited.
        /// </summary>
        ///
        /// <remarks>
        ///   It is not necessary to explicitly unregister this handler; it will be automatically unregistered when
        ///   <see cref="CloseAsync" /> or <see cref="DisposeAsync" /> is invoked.
        /// </remarks>
        ///
        /// <exception cref="ArgumentException">If an attempt is made to remove a handler that doesn't match the current handler registered.</exception>
        /// <exception cref="NotSupportedException">If an attempt is made to add or remove a handler while the processor is running.</exception>
        /// <exception cref="NotSupportedException">If an attempt is made to add a handler when one is currently registered.</exception>
        ///
        public event Func<SendEventBatchSucceededEventArgs, Task> SendEventBatchSucceededAsync
        {
            add
            {
                Argument.AssertNotNull(value, nameof(SendEventBatchSucceededAsync));

                if (_sendSucceededHandler != default)
                {
                    throw new NotSupportedException(Resources.HandlerHasAlreadyBeenAssigned);
                }

                if (_areHandlersLocked)
                {
                    throw new InvalidOperationException(Resources.CannotChangeHandlersWhenPublishing);
                }

                _sendSucceededHandler = value;
            }

            remove
            {
                Argument.AssertNotNull(value, nameof(SendEventBatchSucceededAsync));

                if (_sendSucceededHandler != value)
                {
                    throw new ArgumentException(Resources.HandlerHasNotBeenAssigned);
                }

                if (_areHandlersLocked)
                {
                    throw new InvalidOperationException(Resources.CannotChangeHandlersWhenPublishing);
                }

                _sendSucceededHandler = default;
            }
        }

        /// <summary>
        ///   Invoked for any batch of events that failed to be published to the Event Hub, this handler must be
        ///   provided before events may be enqueued.
        ///
        ///   It is safe to attempt resending the events by calling <see cref="EnqueueEventAsync(EventData, CancellationToken)" /> or <see cref="EnqueueEventAsync(EventData, EnqueueEventOptions, CancellationToken)" /> from within
        ///   this handler.  It is important to note that doing so will place them at the end of the buffer; the original order will not be maintained.
        ///
        ///   It is not recommended to invoke <see cref="CloseAsync" /> or <see cref="DisposeAsync" /> from this handler; doing so may result
        ///   in a deadlock scenario if those calls are awaited.
        /// </summary>
        ///
        /// <remarks>
        ///   Should a transient failure occur during publishing, this handler will not be invoked immediately; it is only
        ///   invoked after applying the retry policy and all eligible retries have been exhausted.  Should publishing succeed
        ///   during a retry attempt, this handler is not invoked.
        ///
        ///   Since applications do not have deterministic control over failed batches, it is recommended that the application
        ///   set a generous number of retries and try timeout interval in the <see cref="EventHubProducerClientOptions.RetryOptions"/>.
        ///   Doing so will allow the <see cref="EventHubBufferedProducerClient" /> a higher chance to recover from transient failures.  This is
        ///   especially important when ensuring the order of events is needed.
        ///
        ///   It is not necessary to explicitly unregister this handler; it will be automatically unregistered when
        ///   <see cref="CloseAsync" /> or <see cref="DisposeAsync" /> is invoked.
        /// </remarks>
        ///
        /// <exception cref="ArgumentException">If an attempt is made to remove a handler that doesn't match the current handler registered.</exception>
        /// <exception cref="NotSupportedException">If an attempt is made to add or remove a handler while the processor is running.</exception>
        /// <exception cref="NotSupportedException">If an attempt is made to add a handler when one is currently registered.</exception>
        ///
        /// <seealso cref="EventHubsRetryOptions" />
        ///
        public event Func<SendEventBatchFailedEventArgs, Task> SendEventBatchFailedAsync
        {
            add
            {
                Argument.AssertNotNull(value, nameof(SendEventBatchFailedAsync));

                if (_sendFailedHandler != default)
                {
                    throw new NotSupportedException(Resources.HandlerHasAlreadyBeenAssigned);
                }

                if (_areHandlersLocked)
                {
                    throw new InvalidOperationException(Resources.CannotChangeHandlersWhenPublishing);
                }

                _sendFailedHandler = value;
            }

            remove
            {
                Argument.AssertNotNull(value, nameof(SendEventBatchFailedAsync));

                if (_sendFailedHandler != value)
                {
                    throw new ArgumentException(Resources.HandlerHasNotBeenAssigned);
                }

                if (_areHandlersLocked)
                {
                    throw new InvalidOperationException(Resources.CannotChangeHandlersWhenPublishing);
                }

                _sendFailedHandler = default;
            }
        }

        /// <summary>
        ///    Initializes a new instance of the <see cref="EventHubBufferedProducerClient" /> class.
        /// </summary>
        ///
        /// <param name="connectionString">The connection string to use for connecting to the Event Hubs namespace; it is expected that the Event Hub name and the shared key properties are contained in this connection string.</param>
        ///
        /// <remarks>
        ///   If the connection string is copied from the Event Hubs namespace, it will likely not contain the name of the desired Event Hub,
        ///   which is needed.  In this case, the name can be added manually by adding ";EntityPath=[[ EVENT HUB NAME ]]" to the end of the
        ///   connection string.  For example, ";EntityPath=telemetry-hub".
        ///
        ///   If you have defined a shared access policy directly on the Event Hub itself, then copying the connection string from that
        ///   Event Hub will result in a connection string that contains the name.
        /// </remarks>
        ///
        /// <seealso href="https://docs.microsoft.com/en-us/azure/event-hubs/event-hubs-get-connection-string">How to get an Event Hubs connection string</seealso>
        ///
        public EventHubBufferedProducerClient(string connectionString) : this(connectionString, null, null)
        {
        }

        /// <summary>
        ///   Initializes a new instance of the <see cref="EventHubBufferedProducerClient" /> class.
        /// </summary>
        ///
        /// <param name="connectionString">The connection string to use for connecting to the Event Hubs namespace; it is expected that the Event Hub name and the shared key properties are contained in this connection string.</param>
        /// <param name="clientOptions">A set of <see cref="EventHubBufferedProducerClientOptions"/> to apply when configuring the buffered producer.</param>
        ///
        /// <remarks>
        ///   If the connection string is copied from the Event Hubs namespace, it will likely not contain the name of the desired Event Hub,
        ///   which is needed.  In this case, the name can be added manually by adding ";EntityPath=[[ EVENT HUB NAME ]]" to the end of the
        ///   connection string.  For example, ";EntityPath=telemetry-hub".
        ///
        ///   If you have defined a shared access policy directly on the Event Hub itself, then copying the connection string from that
        ///   Event Hub will result in a connection string that contains the name.
        /// </remarks>
        ///
        /// <seealso href="https://docs.microsoft.com/en-us/azure/event-hubs/event-hubs-get-connection-string">How to get an Event Hubs connection string</seealso>
        ///
        public EventHubBufferedProducerClient(string connectionString,
                                              EventHubBufferedProducerClientOptions clientOptions) : this(connectionString, null, clientOptions)
        {
        }

        /// <summary>
        ///   Initializes a new instance of the <see cref="EventHubBufferedProducerClient" /> class.
        /// </summary>
        ///
        /// <param name="connectionString">The connection string to use for connecting to the Event Hubs namespace; it is expected that the Event Hub name and the shared key properties are contained in this connection string.</param>
        /// <param name="eventHubName">The name of the specific Event Hub to associate the producer with.</param>
        ///
        /// <remarks>
        ///   If the connection string is copied from the Event Hub itself, it will contain the name of the desired Event Hub,
        ///   and can be used directly without passing the <paramref name="eventHubName" />.  The name of the Event Hub should be
        ///   passed only once, either as part of the connection string or separately.
        /// </remarks>
        ///
        /// <seealso href="https://docs.microsoft.com/en-us/azure/event-hubs/event-hubs-get-connection-string">How to get an Event Hubs connection string</seealso>
        ///
        public EventHubBufferedProducerClient(string connectionString,
                                              string eventHubName) : this(connectionString, eventHubName, default)
        {
        }

        /// <summary>
        ///   Initializes a new instance of the <see cref="EventHubBufferedProducerClient" /> class.
        /// </summary>
        ///
        /// <param name="connectionString">The connection string to use for connecting to the Event Hubs namespace; it is expected that the Event Hub name and the shared key properties are contained in this connection string.</param>
        /// <param name="eventHubName">The name of the specific Event Hub to associate the producer with.</param>
        /// <param name="clientOptions">A set of <see cref="EventHubBufferedProducerClientOptions"/> to apply when configuring the buffered producer.</param>
        ///
        /// <remarks>
        ///   If the connection string is copied from the Event Hub itself, it will contain the name of the desired Event Hub,
        ///   and can be used directly without passing the <paramref name="eventHubName" />.  The name of the Event Hub should be
        ///   passed only once, either as part of the connection string or separately.
        /// </remarks>
        ///
        /// <seealso href="https://docs.microsoft.com/en-us/azure/event-hubs/event-hubs-get-connection-string">How to get an Event Hubs connection string</seealso>
        ///
        public EventHubBufferedProducerClient(string connectionString,
                                              string eventHubName,
                                              EventHubBufferedProducerClientOptions clientOptions) : this(clientOptions)
        {
            Argument.AssertNotNullOrEmpty(connectionString, nameof(connectionString));
            _producer = new EventHubProducerClient(connectionString, eventHubName, (clientOptions ?? DefaultOptions).ToEventHubProducerClientOptions());
        }

        /// <summary>
        ///   Initializes a new instance of the <see cref="EventHubBufferedProducerClient" /> class.
        /// </summary>
        ///
        /// <param name="fullyQualifiedNamespace">The fully qualified Event Hubs namespace to connect to.  This is likely to be similar to <c>{yournamespace}.servicebus.windows.net</c>.</param>
        /// <param name="eventHubName">The name of the specific Event Hub to associate the producer with.</param>
        /// <param name="credential">The shared access key credential to use for authorization.  Access controls may be specified by the Event Hubs namespace or the requested Event Hub, depending on Azure configuration.</param>
        /// <param name="clientOptions">A set of <see cref="EventHubBufferedProducerClientOptions"/> to apply when configuring the producer.</param>
        ///
        public EventHubBufferedProducerClient(string fullyQualifiedNamespace,
                                              string eventHubName,
                                              AzureNamedKeyCredential credential,
                                              EventHubBufferedProducerClientOptions clientOptions = default) : this(fullyQualifiedNamespace, eventHubName, (object)credential, clientOptions)
        {
        }

        /// <summary>
        ///   Initializes a new instance of the <see cref="EventHubBufferedProducerClient" /> class.
        /// </summary>
        ///
        /// <param name="fullyQualifiedNamespace">The fully qualified Event Hubs namespace to connect to.  This is likely to be similar to <c>{yournamespace}.servicebus.windows.net</c>.</param>
        /// <param name="eventHubName">The name of the specific Event Hub to associate the producer with.</param>
        /// <param name="credential">The shared access key credential to use for authorization.  Access controls may be specified by the Event Hubs namespace or the requested Event Hub, depending on Azure configuration.</param>
        /// <param name="clientOptions">A set of <see cref="EventHubBufferedProducerClientOptions"/> to apply when configuring the producer.</param>
        ///
        public EventHubBufferedProducerClient(string fullyQualifiedNamespace,
                                              string eventHubName,
                                              AzureSasCredential credential,
                                              EventHubBufferedProducerClientOptions clientOptions = default) : this(fullyQualifiedNamespace, eventHubName, (object)credential, clientOptions)
        {
        }

        /// <summary>
        ///   Initializes a new instance of the <see cref="EventHubBufferedProducerClient" /> class.
        /// </summary>
        ///
        /// <param name="fullyQualifiedNamespace">The fully qualified Event Hubs namespace to connect to.  This is likely to be similar to <c>{yournamespace}.servicebus.windows.net</c>.</param>
        /// <param name="eventHubName">The name of the specific Event Hub to associate the producer with.</param>
        /// <param name="credential">The shared access key credential to use for authorization.  Access controls may be specified by the Event Hubs namespace or the requested Event Hub, depending on Azure configuration.</param>
        /// <param name="clientOptions">A set of <see cref="EventHubBufferedProducerClientOptions"/> to apply when configuring the producer.</param>
        ///
        public EventHubBufferedProducerClient(string fullyQualifiedNamespace,
                                              string eventHubName,
                                              TokenCredential credential,
                                              EventHubBufferedProducerClientOptions clientOptions = default) : this(fullyQualifiedNamespace, eventHubName, (object)credential, clientOptions)
        {
        }

        /// <summary>
        ///   Initializes a new instance of the <see cref="EventHubBufferedProducerClient" /> class.
        /// </summary>
        ///
        /// <param name="connection">The <see cref="EventHubConnection" /> connection to use for communication with the Event Hubs service.</param>
        /// <param name="clientOptions">A set of <see cref="EventHubBufferedProducerClientOptions"/> to apply when configuring the producer.</param>
        ///
        public EventHubBufferedProducerClient(EventHubConnection connection,
                                              EventHubBufferedProducerClientOptions clientOptions = default) : this(clientOptions)
        {
            _producer = new EventHubProducerClient(connection, (clientOptions ?? DefaultOptions).ToEventHubProducerClientOptions());
        }

        /// <summary>
        ///   Initializes a new instance of the <see cref="EventHubBufferedProducerClient" /> class.
        /// </summary>
        ///
        /// <param name="producer">The <see cref="EventHubProducerClient" />  to use for delegating Event Hubs service operations to.</param>
        /// <param name="clientOptions">A set of <see cref="EventHubBufferedProducerClientOptions"/> to apply when configuring the producer.</param>
        ///
        /// <remarks>
        ///   This constructor is intended to be used internally for functional
        ///   testing only.
        /// </remarks>
        ///
        internal EventHubBufferedProducerClient(EventHubProducerClient producer,
                                                EventHubBufferedProducerClientOptions clientOptions = default) : this(clientOptions)
        {
            _producer = producer;
        }

        /// <summary>
        ///   Used for mocking the producer for testing purposes.
        /// </summary>
        ///
        protected EventHubBufferedProducerClient()
        {
        }

        /// <summary>
        ///   Initializes a new instance of the <see cref="EventHubBufferedProducerClient" /> class.
        /// </summary>
        ///
        /// <param name="fullyQualifiedNamespace">The fully qualified Event Hubs namespace to connect to.  This is likely to be similar to <c>{yournamespace}.servicebus.windows.net</c>.</param>
        /// <param name="eventHubName">The name of the specific Event Hub to associate the producer with.</param>
        /// <param name="credential">The credential to use for authorization.  This may be of type <see cref="TokenCredential" />, <see cref="AzureSasCredential" />, or <see cref="AzureNamedKeyCredential" />.</param>
        /// <param name="clientOptions">A set of <see cref="EventHubBufferedProducerClientOptions"/> to apply when configuring the producer.</param>
        ///
        private EventHubBufferedProducerClient(string fullyQualifiedNamespace,
                                               string eventHubName,
                                               object credential,
                                               EventHubBufferedProducerClientOptions clientOptions = default) : this(clientOptions)
        {
            Argument.AssertWellFormedEventHubsNamespace(fullyQualifiedNamespace, nameof(fullyQualifiedNamespace));
            Argument.AssertNotNullOrEmpty(eventHubName, nameof(eventHubName));
            Argument.AssertNotNull(credential, nameof(credential));

            var options = (clientOptions ?? DefaultOptions).ToEventHubProducerClientOptions();

            _producer = credential switch
            {
                TokenCredential tokenCred => new EventHubProducerClient(fullyQualifiedNamespace, eventHubName, tokenCred, options),
                AzureSasCredential sasCred => new EventHubProducerClient(fullyQualifiedNamespace, eventHubName, sasCred, options),
                AzureNamedKeyCredential keyCred =>  new EventHubProducerClient(fullyQualifiedNamespace, eventHubName, keyCred, options),
                _ => throw new ArgumentException(Resources.UnsupportedCredential, nameof(credential))
            };
        }

        /// <summary>
        ///   Initializes a new instance of the <see cref="EventHubBufferedProducerClient" /> class.
        /// </summary>
        ///
        /// <param name="options">>A set of <see cref="EventHubBufferedProducerClientOptions"/> to apply when configuring the producer.</param>
        ///
        private EventHubBufferedProducerClient(EventHubBufferedProducerClientOptions options)
        {
            _options = options?.Clone() ?? DefaultOptions;
        }

        /// <summary>
        ///   The number of events that are buffered and waiting to be published for a given partition.
        /// </summary>
        ///
        /// <param name="partitionId">The identifier of the partition.</param>
        ///
        public virtual int GetBufferedEventCount(string partitionId)
        {
            Argument.AssertNotClosed(_isClosed, nameof(EventHubBufferedProducerClient));
            Argument.AssertNotNullOrEmpty(partitionId, nameof(partitionId));

            if (_activePartitionPublishingStateMap.TryGetValue(partitionId, out var publisher))
            {
                return publisher.BufferedEventCount;
            }

            return 0;
        }

        /// <summary>
        ///   Retrieves information about the Event Hub that the connection is associated with, including
        ///   the number of partitions present and their identifiers.
        /// </summary>
        ///
        /// <param name="cancellationToken">An optional <see cref="CancellationToken" /> instance to signal the request to cancel the operation.</param>
        ///
        /// <returns>The set of information for the Event Hub that this client is associated with.</returns>
        ///
        public virtual async Task<EventHubProperties> GetEventHubPropertiesAsync(CancellationToken cancellationToken = default)
        {
            Argument.AssertNotClosed(_isClosed, nameof(EventHubBufferedProducerClient));
            return await _producer.GetEventHubPropertiesAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        ///   Retrieves the set of identifiers for the partitions of an Event Hub.
        /// </summary>
        ///
        /// <param name="cancellationToken">An optional <see cref="CancellationToken" /> instance to signal the request to cancel the operation.</param>
        ///
        /// <returns>The set of identifiers for the partitions within the Event Hub that this client is associated with.</returns>
        ///
        /// <remarks>
        ///   This method is synonymous with invoking <see cref="GetEventHubPropertiesAsync(CancellationToken)" /> and reading the <see cref="EventHubProperties.PartitionIds" />
        ///   property that is returned. It is offered as a convenience for quick access to the set of partition identifiers for the associated Event Hub.
        ///   No new or extended information is presented.
        /// </remarks>
        ///
        public virtual async Task<string[]> GetPartitionIdsAsync(CancellationToken cancellationToken = default)
        {
            Argument.AssertNotClosed(_isClosed, nameof(EventHubBufferedProducerClient));
            return await _producer.GetPartitionIdsAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        ///   Retrieves information about a specific partition for an Event Hub, including elements that describe the available
        ///   events in the partition event stream.
        /// </summary>
        ///
        /// <param name="partitionId">The unique identifier of a partition associated with the Event Hub.</param>
        /// <param name="cancellationToken">An optional <see cref="CancellationToken" /> instance to signal the request to cancel the operation.</param>
        ///
        /// <returns>The set of information for the requested partition under the Event Hub this client is associated with.</returns>
        ///
        public virtual async Task<PartitionProperties> GetPartitionPropertiesAsync(string partitionId,
                                                                                   CancellationToken cancellationToken = default)
        {
            Argument.AssertNotClosed(_isClosed, nameof(EventHubBufferedProducerClient));
            return await _producer.GetPartitionPropertiesAsync(partitionId, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        ///   Enqueues an <see cref="EventData"/> into the buffer to be published to the Event Hub.  If there is no capacity in
        ///   the buffer when this method is invoked, it will wait for space to become available and ensure that the <paramref name="eventData"/>
        ///   has been enqueued.
        ///
        ///   When this call returns, the <paramref name="eventData" /> has been accepted into the buffer, but it may not have been published yet.
        ///   Publishing will take place at a nondeterministic point in the future as the buffer is processed.
        /// </summary>
        ///
        /// <param name="eventData">The event to be enqueued into the buffer and, later, published.</param>
        /// <param name="cancellationToken">An optional <see cref="CancellationToken"/> instance to signal the request to cancel the operation.</param>
        ///
        /// <returns>The total number of events that are currently buffered and waiting to be published, across all partitions.</returns>
        ///
        /// <exception cref="InvalidOperationException">Occurs when no <see cref="SendEventBatchFailedAsync" /> handler is currently registered.</exception>
        ///
        /// <remarks>
        ///   Upon the first attempt to enqueue an event, the <see cref="SendEventBatchSucceededAsync" /> and <see cref="SendEventBatchFailedAsync" /> handlers
        ///   can no longer be changed.
        /// </remarks>
        ///
        public virtual Task<int> EnqueueEventAsync(EventData eventData,
                                                   CancellationToken cancellationToken = default) => EnqueueEventAsync(eventData, default, cancellationToken);

        /// <summary>
        ///   Enqueues an <see cref="EventData"/> into the buffer to be published to the Event Hub.  If there is no capacity in
        ///   the buffer when this method is invoked, it will wait for space to become available and ensure that the <paramref name="eventData"/>
        ///   has been enqueued.
        ///
        ///   When this call returns, the <paramref name="eventData" /> has been accepted into the buffer, but it may not have been published yet.
        ///   Publishing will take place at a nondeterministic point in the future as the buffer is processed.
        /// </summary>
        ///
        /// <param name="eventData">The event to be enqueued into the buffer and, later, published.</param>
        /// <param name="options">The set of options to apply when publishing this event.</param>
        /// <param name="cancellationToken">An optional <see cref="CancellationToken"/> instance to signal the request to cancel the operation.</param>
        ///
        /// <returns>The total number of events that are currently buffered and waiting to be published, across all partitions.</returns>
        ///
        /// <exception cref="InvalidOperationException">Occurs when no <see cref="SendEventBatchFailedAsync" /> handler is currently registered.</exception>
        /// <exception cref="InvalidOperationException">Occurs when both a partition identifier and partition key have been specified in the <paramref name="options"/>.</exception>
        /// <exception cref="InvalidOperationException">Occurs when an invalid partition identifier has been specified in the <paramref name="options"/>.</exception>
        ///
        /// <remarks>
        ///   Upon the first attempt to enqueue an event, the <see cref="SendEventBatchSucceededAsync" /> and <see cref="SendEventBatchFailedAsync" /> handlers
        ///   can no longer be changed.
        /// </remarks>
        ///
        public virtual async Task<int> EnqueueEventAsync(EventData eventData,
                                                         EnqueueEventOptions options,
                                                         CancellationToken cancellationToken = default)
        {
            (var partitionId, var partitionKey) = EnqueueEventOptions.DeconstructOrUseDefaultAttributes(options);

            Argument.AssertNotClosed(_isClosed, nameof(EventHubBufferedProducerClient));
            Argument.AssertNotNull(eventData, nameof(eventData));
            AssertSinglePartitionReference(partitionId, partitionKey);
            AssertRequiredHandlerSetForEnqueue(_sendFailedHandler, nameof(SendEventBatchFailedAsync));

            _areHandlersLocked = true;
            cancellationToken.ThrowIfCancellationRequested<TaskCanceledException>();

            var logPartition = partitionKey ?? partitionId ?? string.Empty;
            var operationId = GenerateOperationId();
            Logger.BufferedProducerEventEnqueueStart(Identifier, EventHubName, logPartition, operationId);

            try
            {
                // If publishing has not been started or is not healthy, attempt to restart it.

                if ((!IsPublishing) || (_producerManagementTask?.IsCompleted ?? false))
                {
                    var releaseGuard = false;

                    try
                    {
                        if (!_stateGuard.Wait(0, cancellationToken))
                        {
                            await _stateGuard.WaitAsync(cancellationToken).ConfigureAwait(false);
                        }

                        releaseGuard = true;

                        // StartPublishingAsync will verify that publishing is not already taking
                        // place and act appropriately if nothing needs to be restarted; there's no need
                        // to perform a double-check of the conditions here after acquiring the semaphore.

                        await StartPublishingAsync(cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (releaseGuard)
                        {
                            _stateGuard.Release();
                        }
                    }
                }

                // If there was a partition identifier requested, validate that it is part of
                // the known set, now that publishing has been started.

                if (!string.IsNullOrEmpty(partitionId))
                {
                    AssertValidPartition(partitionId, _partitionHash);
                }

                // Annotate the event with the current time; this is intended to help ensure that
                // publishing can apply the maximum wait time correctly and will be removed when
                // the event as added to a batch.

                var amqpMessage = eventData.GetRawAmqpMessage();
                amqpMessage.SetEnqueuedTime(GetCurrentTime());

                // If there was a partition key requested, calculate the assigned partition and
                // annotate the event so that it is preserved by the Event Hubs broker.

                if (!string.IsNullOrEmpty(partitionKey))
                {
                    partitionId = AssignPartitionForPartitionKey(partitionKey);
                    amqpMessage.SetPartitionKey(partitionKey);
                }

                // If no partition was assigned, assign one for automatic routing.

                if (string.IsNullOrEmpty(partitionId))
                {
                    partitionId = AssignPartition();
                }

                // Enqueue the event into the channel for the assigned partition.  Note that this call will wait
                // if there is no room in the channel and may take some time to complete.

                var partitionPublisher = _activePartitionPublishingStateMap.GetOrAdd(partitionId, partitionId => new PartitionPublishingState(partitionId, _options));
                var writer = partitionPublisher.PendingEventsWriter;

                await writer.WriteAsync(eventData, cancellationToken).ConfigureAwait(false);

                var count = Interlocked.Increment(ref _totalBufferedEventCount);
                Interlocked.Increment(ref partitionPublisher.BufferedEventCount);

                Logger.BufferedProducerEventEnqueued(Identifier, EventHubName, logPartition, partitionId, operationId, count);
            }
            catch (Exception ex)
            {
                Logger.BufferedProducerEventEnqueueError(Identifier, EventHubName, logPartition, operationId, ex.Message);
                throw;
            }
            finally
            {
                Logger.BufferedProducerEventEnqueueComplete(Identifier, EventHubName, logPartition, operationId);
            }

            return _totalBufferedEventCount;
        }

        /// <summary>
        ///   Enqueues a set of <see cref="EventData"/> into the buffer to be published to the Event Hub.  If there is insufficient capacity in
        ///   the buffer when this method is invoked, it will wait for space to become available and ensure that all <paramref name="events"/>
        ///   in the <paramref name="events"/> set have been enqueued.
        ///
        ///   When this call returns, the <paramref name="events" /> have been accepted into the buffer, but it may not have been published yet.
        ///   Publishing will take place at a nondeterministic point in the future as the buffer is processed.
        /// </summary>
        ///
        /// <param name="events">The set of events to be enqueued into the buffer and, later, published.</param>
        /// <param name="cancellationToken">An optional <see cref="CancellationToken"/> instance to signal the request to cancel the operation.</param>
        ///
        /// <returns>The total number of events that are currently buffered and waiting to be published, across all partitions.</returns>
        ///
        /// <exception cref="InvalidOperationException">Occurs when no <see cref="SendEventBatchFailedAsync" /> handler is currently registered.</exception>
        ///
        /// <remarks>
        ///   Should cancellation or an unexpected exception occur, it is possible for calls to this method to result in a partial failure where some, but not all,
        ///   of the <paramref name="events" /> have enqueued.  For scenarios where it is important to understand whether each individual event has been
        ///   enqueued, it is recommended to call the see <see cref="EnqueueEventAsync(EventData, EnqueueEventOptions, CancellationToken)" /> or
        ///   <see cref="EnqueueEventAsync(EventData, CancellationToken)" /> overloads instead of this method.
        ///
        ///   Upon the first attempt to enqueue events, the <see cref="SendEventBatchSucceededAsync" /> and <see cref="SendEventBatchFailedAsync" /> handlers
        ///   can no longer be changed.
        /// </remarks>
        ///
        public virtual Task<int> EnqueueEventsAsync(IEnumerable<EventData> events,
                                                    CancellationToken cancellationToken = default) => EnqueueEventsAsync(events, default, cancellationToken);

        /// <summary>
        ///   Enqueues a set of <see cref="EventData"/> into the buffer to be published to the Event Hub.  If there is insufficient capacity in
        ///   the buffer when this method is invoked, it will wait for space to become available and ensure that all <paramref name="events"/>
        ///   in the <paramref name="events"/> set have been enqueued.
        ///
        ///   When this call returns, the <paramref name="events"/> have been accepted into the buffer, but it may not have been published yet.
        ///   Publishing will take place at a nondeterministic point in the future as the buffer is processed.
        /// </summary>
        ///
        /// <param name="events">The set of events to be enqueued into the buffer and, later, published.</param>
        /// <param name="options">The set of options to apply when publishing these events.</param>
        /// <param name="cancellationToken">An optional <see cref="CancellationToken"/> instance to signal the request to cancel the operation.</param>
        ///
        /// <returns>The total number of events that are currently buffered and waiting to be published, across all partitions.</returns>
        ///
        /// <exception cref="InvalidOperationException">Occurs when no <see cref="SendEventBatchFailedAsync" /> handler is currently registered.</exception>
        /// <exception cref="InvalidOperationException">Occurs when both a partition identifier and partition key have been specified in the <paramref name="options"/>.</exception>
        /// <exception cref="InvalidOperationException">Occurs when an invalid partition identifier has been specified in the <paramref name="options"/>.</exception>
        ///
        /// <remarks>
        ///   Should cancellation or an unexpected exception occur, it is possible for calls to this method to result in a partial failure where some, but not all,
        ///   of the <paramref name="events" /> have enqueued.  For scenarios where it is important to understand whether each individual event has been
        ///   enqueued, it is recommended to call the see <see cref="EnqueueEventAsync(EventData, EnqueueEventOptions, CancellationToken)" /> or
        ///   <see cref="EnqueueEventAsync(EventData, CancellationToken)" /> overloads instead of this method.
        ///
        ///   Upon the first attempt to enqueue events, the <see cref="SendEventBatchSucceededAsync" /> and <see cref="SendEventBatchFailedAsync" /> handlers
        ///   can no longer be changed.
        /// </remarks>
        ///
        public virtual async Task<int> EnqueueEventsAsync(IEnumerable<EventData> events,
                                                          EnqueueEventOptions options,
                                                          CancellationToken cancellationToken = default)
        {
            (var partitionId, var partitionKey) = EnqueueEventOptions.DeconstructOrUseDefaultAttributes(options);

            Argument.AssertNotClosed(_isClosed, nameof(EventHubBufferedProducerClient));
            Argument.AssertNotNull(events, nameof(events));
            AssertSinglePartitionReference(partitionId, partitionKey);
            AssertRequiredHandlerSetForEnqueue(_sendFailedHandler, nameof(SendEventBatchFailedAsync));

            _areHandlersLocked = true;
            cancellationToken.ThrowIfCancellationRequested<TaskCanceledException>();

            var logPartition = (partitionKey ?? partitionId ?? string.Empty);
            var operationId = GenerateOperationId();
            Logger.BufferedProducerEventEnqueueStart(Identifier, EventHubName, logPartition, operationId);

            try
            {
                // If publishing has not been started or is not healthy, attempt to restart it.

                if ((!IsPublishing) || (_producerManagementTask?.IsCompleted ?? false))
                {
                    var releaseGuard = false;

                    try
                    {
                        if (!_stateGuard.Wait(0, cancellationToken))
                        {
                            await _stateGuard.WaitAsync(cancellationToken).ConfigureAwait(false);
                        }

                        releaseGuard = true;

                        // StartPublishingAsync will verify that publishing is not already taking
                        // place and act appropriately if nothing needs to be restarted; there's no need
                        // to perform a double-check of the conditions here after acquiring the semaphore.

                        await StartPublishingAsync(cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (releaseGuard)
                        {
                            _stateGuard.Release();
                        }
                    }
                }

                // If there was a partition identifier requested, validate that it is part of
                // the known set, now that publishing has been started.

                if (!string.IsNullOrEmpty(partitionId))
                {
                    AssertValidPartition(partitionId, _partitionHash);
                }

                // If there was a partition key requested, calculate the assigned partition.

                if (!string.IsNullOrEmpty(partitionKey))
                {
                    partitionId = AssignPartitionForPartitionKey(partitionKey);
                }

                // If there is a stable partition identifier for all events in the batch, acquire the publisher for it.

                var partitionPublisher = string.IsNullOrEmpty(partitionId)
                    ? null
                    : _activePartitionPublishingStateMap.GetOrAdd(partitionId, partitionId => new PartitionPublishingState(partitionId, _options));

                // Enumerate the events and enqueue them.

                var enqueueTime = GetCurrentTime();

                foreach (var eventData in events)
                {
                    var eventPartitionId = partitionId;
                    var amqpMessage = eventData.GetRawAmqpMessage();

                    // If there is an associated partition key, annotate the event so that it is
                    // preserved by the Event Hubs broker.

                    if (!string.IsNullOrEmpty(partitionKey))
                    {
                        amqpMessage.SetPartitionKey(partitionKey);
                    }

                    // Annotate the event with the current time; this is intended to help ensure that
                    // publishing can apply the maximum wait time correctly and will be removed when
                    // the event as added to a batch.

                    amqpMessage.SetEnqueuedTime(enqueueTime);

                    // If no partition was assigned, assign one for automatic routing.

                    if (string.IsNullOrEmpty(eventPartitionId))
                    {
                        eventPartitionId = AssignPartition();
                    }

                    // Enqueue the event into the channel for the assigned partition.  Note that this call will wait
                    // if there is no room in the channel and may take some time to complete.

                    var publisher = partitionPublisher ?? _activePartitionPublishingStateMap.GetOrAdd(eventPartitionId, partitionId => new PartitionPublishingState(eventPartitionId, _options));
                    var writer = publisher.PendingEventsWriter;

                    await writer.WriteAsync(eventData, cancellationToken).ConfigureAwait(false);

                    var count = Interlocked.Increment(ref _totalBufferedEventCount);
                    Interlocked.Increment(ref publisher.BufferedEventCount);

                    Logger.BufferedProducerEventEnqueued(Identifier, EventHubName, logPartition, eventPartitionId, operationId, count);
                }
            }
            catch (Exception ex)
            {
                Logger.BufferedProducerEventEnqueueError(Identifier, EventHubName, logPartition, operationId, ex.Message);
                throw;
            }
            finally
            {
                Logger.BufferedProducerEventEnqueueComplete(Identifier, EventHubName, logPartition, operationId);
            }

            return _totalBufferedEventCount;
        }

        /// <summary>
        ///   Attempts to publish all events in the buffer immediately.  This may result in multiple batches being published,
        ///   the outcome of each of which will be individually reported by the <see cref="SendEventBatchSucceededAsync" /> and
        ///   <see cref="SendEventBatchFailedAsync" /> handlers.
        ///
        ///   Upon completion of this method, the buffer will be empty.
        /// </summary>
        ///
        /// <param name="cancellationToken">An optional <see cref="CancellationToken" /> instance to signal the request to cancel the operation.</param>
        ///
        public virtual async Task FlushAsync(CancellationToken cancellationToken = default)
        {
            Argument.AssertNotClosed(_isClosed, nameof(EventHubBufferedProducerClient));

            if (!_stateGuard.Wait(0, cancellationToken))
            {
                await _stateGuard.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                Argument.AssertNotClosed(_isClosed, nameof(EventHubBufferedProducerClient));
                await FlushInternalAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // If we reached the try block without an exception, it is safe to assume the guard is held.

                _stateGuard.Release();
            }
        }

        /// <summary>
        ///   Closes the producer and performs the tasks needed to clean up all the resources used by the <see cref="EventHubBufferedProducerClient"/>.
        /// </summary>
        ///
        /// <param name="flush"><c>true</c> if all buffered events that are pending should be published before closing; <c>false</c> to abandon all events and close immediately.</param>
        /// <param name="cancellationToken">An optional <see cref="CancellationToken" /> instance to signal the request to cancel the operation.</param>
        ///
        /// <returns>A task to be resolved on when the operation has completed.</returns>
        ///
        /// <remarks>
        ///   This method will automatically unregister the <see cref="SendEventBatchSucceededAsync"/> and <see cref="SendEventBatchFailedAsync"/> handlers.
        /// </remarks>
        ///
        public virtual async Task CloseAsync(bool flush = true,
                                             CancellationToken cancellationToken = default)
        {
            if (_isClosed)
            {
                return;
            }

            var guardHeld = false;
            var capturedExceptions = default(List<Exception>);

            try
            {
                if (!_stateGuard.Wait(0, cancellationToken))
                {
                    await _stateGuard.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                // If we've reached this point without an exception, the guard is held.

                guardHeld = true;

                if (_isClosed)
                {
                    return;
                }

                _isClosed = true;
                Logger.ClientCloseStart(nameof(EventHubBufferedProducerClient), EventHubName, Identifier);

                if (IsPublishing)
                {
                    try
                    {
                        if (flush)
                        {
                            await FlushInternalAsync(CancellationToken.None).ConfigureAwait(false);
                        }
                        else
                        {
                           await ClearInternalAsync(CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        // If flushing, the exception should cause closing to fail to protect against data loss.  State should be
                        // reset so that applications can attempt to close/flush again.  This should be rare and limited to unexpected
                        // scenarios, as normal exceptions during send operations will be surfaced through the handler.

                        if (flush)
                        {
                            _isClosed = false;
                            throw;
                        }
                        else
                        {
                            // When clearing, the exception is non-blocking; log and continue.

                            Logger.ClientCloseError(nameof(EventHubBufferedProducerClient), EventHubName, Identifier, ex.Message);
                        }
                    }
                }

                // Stop processing and close the producer.  Capture exceptions for later bubbling to allow
                // cleanup and disposal to complete.

                try
                {
                    await StopPublishingAsync(false, CancellationToken.None).ConfigureAwait(false);
                    await _producer.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.ClientCloseError(nameof(EventHubBufferedProducerClient), EventHubName, Identifier, ex.Message);
                    (capturedExceptions ??= new List<Exception>()).Add(ex);
                }

                // Clean up partition state.

                foreach (var pair in _activePartitionPublishingStateMap)
                {
                    // Dispose for the partition publishers will log exceptions and
                    // avoid surfacing them, as processing is stopping.

                    try
                    {
                        _activePartitionPublishingStateMap.TryRemove(pair.Key, out _);
                        pair.Value.Dispose();
                    }
                    catch (Exception ex)
                    {
                       Logger.ClientCloseError(nameof(EventHubBufferedProducerClient), EventHubName, Identifier, ex.Message);
                       (capturedExceptions ??= new List<Exception>()).Add(ex);
                    }
                }

                _activePartitionPublishingStateMap.Clear();

                // Unregister the event handlers.

                _areHandlersLocked = false;

                if (_sendSucceededHandler != null)
                {
                    SendEventBatchSucceededAsync -= _sendSucceededHandler;
                    _sendSucceededHandler = null;
                }

                if (_sendFailedHandler != null)
                {
                    SendEventBatchFailedAsync -= _sendFailedHandler;
                    _sendFailedHandler = null;
                }
            }
            catch (Exception ex)
            {
                Logger.ClientCloseError(nameof(EventHubBufferedProducerClient), EventHubName, Identifier, ex.Message);
                throw;
            }
            finally
            {
                if (guardHeld)
                {
                    _stateGuard.Release();
                }

                //TODO: Evaluate whether to use this here, depending on whether Flush failures abort closing.
                // _stateGuard.Dispose();
                Logger.ClientCloseComplete(nameof(EventHubBufferedProducerClient), EventHubName, Identifier);
            }

            // Surface any exceptions that were captured during cleanup.

            if (capturedExceptions?.Count == 1)
            {
                ExceptionDispatchInfo.Capture(capturedExceptions[0]).Throw();
            }
            else if (capturedExceptions is not null)
            {
                throw new AggregateException(capturedExceptions);
            };
        }

        /// <summary>
        ///   Closes the producer and performs the tasks needed to clean up all the resources used by the <see cref="EventHubBufferedProducerClient"/>.
        /// </summary>
        ///
        /// <remarks>
        ///   Calling this method will also invoke <see cref="FlushInternalAsync(CancellationToken)"/>, which will attempt to publish any events that are still pending,
        ///   and finish any active sending.  It will also automatically unregister the <see cref="SendEventBatchSucceededAsync"/> and <see cref="SendEventBatchFailedAsync"/>
        ///   handlers.
        ///
        ///   This method is identical to <see cref="CloseAsync(bool, CancellationToken)"/> and either can be used to send pending events and clean up resources.
        /// </remarks>
        ///
        /// <returns>A task to be resolved on when the operation has completed.</returns>
        ///
        public virtual async ValueTask DisposeAsync()
        {
            await CloseAsync(true).ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        ///   Determines whether the specified <see cref="System.Object" /> is equal to this instance.
        /// </summary>
        ///
        /// <param name="obj">The <see cref="System.Object" /> to compare with this instance.</param>
        ///
        /// <returns><c>true</c> if the specified <see cref="System.Object" /> is equal to this instance; otherwise, <c>false</c>.</returns>
        ///
        [EditorBrowsable(EditorBrowsableState.Never)]
        public override bool Equals(object obj) => base.Equals(obj);

        /// <summary>
        ///   Returns a hash code for this instance.
        /// </summary>
        ///
        /// <returns>A hash code for this instance, suitable for use in hashing algorithms and data structures like a hash table.</returns>
        ///
        [EditorBrowsable(EditorBrowsableState.Never)]
        public override int GetHashCode() => base.GetHashCode();

        /// <summary>
        ///   Converts the instance to string representation.
        /// </summary>
        ///
        /// <returns>A <see cref="System.String" /> that represents this instance.</returns>
        ///
        [EditorBrowsable(EditorBrowsableState.Never)]
        public override string ToString() => base.ToString();

        /// <summary>
        ///   Cancels any active publishing and abandons all events in the buffer that are waiting to be published.
        ///   Upon completion of this method, the buffer will be empty.
        /// </summary>
        ///
        /// <param name="cancellationToken">An optional <see cref="CancellationToken" /> instance to signal the request to cancel the operation.</param>
        ///
        /// <remarks>
        ///   This method will modify class-level state and should be synchronized.  It is assumed that callers hold responsibility for
        ///   ensuring synchronization concerns; this method should be invoked after any primitives have been acquired.
        ///
        ///   Callers are also assumed to own responsibility for any validation that the client is in the intended state before calling.
        /// </remarks>
        ///
        internal virtual Task ClearInternalAsync(CancellationToken cancellationToken = default)
        {
            // ======================================================================
            //  NOTE:
            //    This method is currently just a stub; full functionality will be
            //    added in a later set of changes.
            // ======================================================================

            return Task.CompletedTask;
        }

        /// <summary>
        ///   Attempts to publish all events in the buffer immediately.  This may result in multiple batches being published,
        ///   the outcome of each of which will be individually reported by the <see cref="SendEventBatchSucceededAsync" /> and
        ///   <see cref="SendEventBatchFailedAsync" /> handlers.
        ///
        ///   Upon completion of this method, the buffer will be empty.
        /// </summary>
        ///
        /// <param name="cancellationToken">An optional <see cref="CancellationToken" /> instance to signal the request to cancel the operation.</param>
        ///
        /// <remarks>
        ///   This method will modify class-level state and should be synchronized.  It is assumed that callers hold responsibility for
        ///   ensuring synchronization concerns; this method should be invoked after any primitives have been acquired.
        ///
        ///   Callers are also assumed to own responsibility for any validation that the client is in the intended state before calling.
        /// </remarks>
        ///
        internal virtual async Task FlushInternalAsync(CancellationToken cancellationToken = default)
        {
            // ======================================================================
            //  NOTE:
            //    This method is currently just a stub; full functionality will be
            //    added in a later set of changes.
            // ======================================================================

            // If publishing is taking place, but the management task has completed,
            // restart publishing to ensure that send operations are taking place.

            if ((IsPublishing) && (_producerManagementTask?.IsCompleted ?? false))
            {
                await StartPublishingAsync(cancellationToken).ConfigureAwait(false);
            }

            //TODO: Implement Flush
        }

        /// <summary>
        ///   Attempts to publish a batch to the requested partition, assuming events are available
        ///   and the concurrency guard can be acquired within the available time limit.
        /// </summary>
        ///
        /// <param name="partitionState">The state of publishing for the partition.</param>
        /// <param name="releaseGuard"><c>true</c> if the <see cref="PartitionPublishingState.PartitionGuard" /> should be released after publishing; otherwise, <c>false</c>.</param>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> instance to signal the request to cancel publishing.</param>
        ///
        /// <remarks>
        ///   This method has responsibility for invoking the event handlers to communicate
        ///   success or failure of the operation.
        /// </remarks>
        ///
        internal virtual async Task PublishBatchToPartition(PartitionPublishingState partitionState,
                                                            bool releaseGuard,
                                                            CancellationToken cancellationToken)
        {
            var batchEventCount = 0;
            var operationId = GenerateOperationId();
            var partitionId = partitionState.PartitionId;
            var publishWatch = default(ValueStopwatch);
            var batch = default(EventDataBatch);

            Logger.BufferedProducerEventBatchPublishStart(Identifier, EventHubName, partitionState.PartitionId, operationId);

            try
            {
                // Determine the intervals for the time limit on building the batch and for delaying between empty reads.

                var totalWaitTime = _options.MaximumWaitTime ?? Timeout.InfiniteTimeSpan;
                var remainingWaitTime = totalWaitTime;
                var delayInterval = CalculateDelay(totalWaitTime, DefaultPublishingDelayInterval);

                // The wait time constraint should not consider creating the batch; start tracking after the batch is available to build.

                batch = await _producer.CreateBatchAsync(new CreateBatchOptions { PartitionId = partitionId }, cancellationToken).ConfigureAwait(false);
                publishWatch = ValueStopwatch.StartNew();

                // Build the batch, stopping either when it is full or when the allowable wait time
                // has been exceeded.

                while (ShouldWait(remainingWaitTime, MinimumPublishingWaitInterval))
                {
                    if (partitionState.TryReadEvent(out var currentEvent))
                    {
                        if (batch.TryAdd(currentEvent))
                        {
                            ++batchEventCount;
                            Logger.BufferedProducerEventBatchPublishEventAdded(Identifier, EventHubName, partitionId, operationId, batchEventCount, publishWatch.GetElapsedTime().TotalSeconds);
                        }
                        else
                        {
                            // If this event is the first for the batch, then it is too large to ever successfully publish.  Because this event is poison
                            // it should not be stashed.  Since it was not added to the batch, the normal error handler path won't properly report it.
                            // Instead perform the logging and handler invocation inline and then exit.

                            if (batch.Count == 0)
                            {
                                Interlocked.Decrement(ref partitionState.BufferedEventCount);
                                Interlocked.Decrement(ref _totalBufferedEventCount);

                                var message = string.Format(CultureInfo.InvariantCulture, Resources.EventTooLargeMask, EventHubName, batch.MaximumSizeInBytes);
                                var exception = new EventHubsException(EventHubName, message, EventHubsException.FailureReason.MessageSizeExceeded);

                                var eventList = new List<EventData>(1);
                                eventList.Add(currentEvent);

                                Logger.BufferedProducerEventBatchPublishError(Identifier, EventHubName, partitionId, operationId, message);

                                // Handler invocation is performed in the background as a fire-and-forget operation.  Exceptions in the handler
                                // are logged as part of the invocation.

                                _ = InvokeOnSendFailedAsync(eventList, exception, partitionId);

                                return;
                            }

                            // The last read event could not fit in the batch; stash it for the next reader so that it isn't lost.

                            partitionState.StashEvent(currentEvent);

                            // The batch is full; break out of the batch building loop and move onto publishing.

                            break;
                        }
                    }
                    else
                    {
                        // If no event was available, delay for a short period to avoid a tight loop while attempting to read.  At
                        // this point, the remaining time has not been updated, but the attempt to read was a quick synchronous operation,
                        // so the lack of precision is not a concern.

                        delayInterval = CalculateDelay(remainingWaitTime, delayInterval);
                        Logger.BufferedProducerEventBatchPublishNoEventRead(Identifier, EventHubName, partitionId, operationId, delayInterval.TotalSeconds, publishWatch.GetElapsedTime().TotalSeconds);

                        if (ShouldWait(remainingWaitTime, MinimumPublishingWaitInterval))
                        {
                            cancellationToken.ThrowIfCancellationRequested<TaskCanceledException>();
                            await Task.Delay(delayInterval, cancellationToken).ConfigureAwait(false);
                        }
                    }

                    // Adjust the remaining time based on the current total amount of time spent building the batch; if
                    // no wait time was specified, this will remain infinite.

                    remainingWaitTime = totalWaitTime.CalculateRemaining(publishWatch.GetElapsedTime());
                }

                // If there were events added to the batch, publish them.

                if (batch.Count > 0)
                {
                    // Handler invocation is performed in the background as a fire-and-forget operation.  Exceptions in the handler
                    // are logged as part of the invocation.

                    await _producer.SendAsync(batch, cancellationToken).ConfigureAwait(false);
                    _ = InvokeOnSendSucceededAsync(batch.AsEnumerable<EventData>().ToList(), partitionState.PartitionId);
                }
            }
            catch (Exception ex)
            {
                Logger.BufferedProducerEventBatchPublishError(Identifier, EventHubName, partitionId, operationId, ex.Message);

                // Handler invocation is performed in the background as a fire-and-forget operation.  Exceptions in the handler
                // are logged as part of the invocation.

                if (batch?.Count > 0)
                {
                    _ = InvokeOnSendFailedAsync(batch.AsEnumerable<EventData>().ToList(), ex, partitionId);
                }
            }
            finally
            {
                // Succeed or fail, the batch events are no longer buffered; remove them from the partition state and the total.

                var delta = (batchEventCount * -1);

                Interlocked.Add(ref partitionState.BufferedEventCount, delta);
                Interlocked.Add(ref _totalBufferedEventCount, delta);

                batch?.Dispose();

                if (releaseGuard)
                {
                    partitionState.PartitionGuard.Release();
                }

                var duration = publishWatch.IsActive ? publishWatch.GetElapsedTime().TotalSeconds : 0;
                Logger.BufferedProducerEventBatchPublishComplete(Identifier, EventHubName, partitionId, operationId, batchEventCount, duration);
            }
        }

        /// <summary>
        ///   Gets the current date and time.
        /// </summary>
        ///
        /// <returns>The <see cref="DateTimeOffset" /> representing the current time.</returns>
        ///
        internal virtual DateTimeOffset GetCurrentTime() => DateTimeOffset.UtcNow;

        /// <summary>
        ///   Responsible for raising the <see cref="SendEventBatchSucceededAsync"/> event upon the successful publishing
        ///   of a batch of events.
        /// </summary>
        ///
        /// <param name="events">The set of events belonging to the batch that was successfully published.</param>
        /// <param name="partitionId">The identifier of the partition that the batch of events was published to.</param>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> instance to signal the request to cancel publishing.</param>
        ///
        protected virtual async Task OnSendSucceededAsync(IReadOnlyList<EventData> events,
                                                          string partitionId,
                                                          CancellationToken cancellationToken = default)
        {
            // Once publishing has started, it's not possible to add/remove handlers; its safe to assume that
            // the reference is valid without caching it.

            if (_sendSucceededHandler == null)
            {
                Logger.BufferedProducerNoPublishEventHandler(Identifier, EventHubName, partitionId);
                return;
            }

            var operationId = GenerateOperationId();
            Logger.BufferedProducerOnSendSucceededStart(Identifier, EventHubName, partitionId, operationId);

            try
            {
                var args = new SendEventBatchSucceededEventArgs(events, partitionId, cancellationToken);
                await _sendSucceededHandler(args).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.BufferedProducerOnSendSucceededError(Identifier, EventHubName, partitionId, operationId, ex.Message);
                throw;
            }
            finally
            {
                Logger.BufferedProducerOnSendSucceededComplete(Identifier, EventHubName, partitionId, operationId);
            }
        }

        /// <summary>
        ///   Responsible for raising the <see cref="SendEventBatchFailedAsync"/> event upon the failed publishing of a
        ///   batch of events, after all eligible retries are exhausted.
        /// </summary>
        ///
        /// <param name="events">The set of events belonging to the batch that failed to be published.</param>
        /// <param name="exception">The <see cref="Exception"/> that was raised when the events failed to publish.</param>
        /// <param name="partitionId">The identifier of the partition that the batch of events was published to.</param>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> instance to signal the request to cancel publishing.</param>
        ///
        protected virtual async Task OnSendFailedAsync(IReadOnlyList<EventData> events,
                                                       Exception exception,
                                                       string partitionId,
                                                       CancellationToken cancellationToken = default)
        {
            // Once publishing has started, it's not possible to add/remove handlers; its safe to assume that
            // the reference is valid without caching it.

            if (_sendFailedHandler == null)
            {
                Logger.BufferedProducerNoPublishEventHandler(Identifier, EventHubName, partitionId);
                return;
            }

            var operationId = GenerateOperationId();
            Logger.BufferedProducerOnSendFailedStart(Identifier, EventHubName, partitionId, operationId);

            try
            {
                var args = new SendEventBatchFailedEventArgs(events, exception, partitionId, cancellationToken);
                await _sendFailedHandler(args).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.BufferedProducerOnSendFailedError(Identifier, EventHubName, partitionId, operationId, ex.Message);
                throw;
            }
            finally
            {
                Logger.BufferedProducerOnSendFailedComplete(Identifier, EventHubName, partitionId, operationId);
            }
        }

        /// <summary>
        ///   Queries for the identifiers of the Event Hub partitions.
        /// </summary>
        ///
        /// <param name="producer">The producer client instance to use for querying.</param>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> instance to signal the request to cancel the query.</param>
        ///
        /// <returns>The set of identifiers for the Event Hub partitions.</returns>
        ///
        protected virtual async Task<string[]> ListPartitionIdsAsync(EventHubProducerClient producer,
                                                                     CancellationToken cancellationToken) =>
            await producer.GetPartitionIdsAsync(cancellationToken).ConfigureAwait(false);

        /// <summary>
        ///   Performs the actions needed to initialize the <see cref="EventHubBufferedProducerClient" /> to begin accepting events to be
        ///   enqueued and published.  If this method is called while processing is active, no action is taken.
        /// </summary>
        ///
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> instance to signal the request to cancel the start operation.  This won't affect background processing once it starts running.</param>
        ///
        /// <remarks>
        ///   This method will modify class-level state and should be synchronized.  It is assumed that callers hold responsibility for
        ///   ensuring synchronization concerns; this method should be invoked after any primitives have been acquired.
        ///
        ///   Callers are also assumed to own responsibility for any validation that the client is in the intended state before calling.
        /// </remarks>
        ///
        private async Task StartPublishingAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested<TaskCanceledException>();
            Logger.BufferedProducerBackgroundProcessingStart(Identifier, EventHubName);

            try
            {
                // If there is already a task running for the background management process,
                // then no further initialization is needed.

                if ((IsPublishing) && (!_producerManagementTask.IsCompleted))
                {
                    return;
                }

                // There should be no cancellation source, but guard against leaking resources in the
                // event of a crash or other exception.

                _backgroundTasksCancellationSource?.Cancel();
                _backgroundTasksCancellationSource?.Dispose();
                _backgroundTasksCancellationSource = new CancellationTokenSource();

                // If there was a task present, then it will have been previously faulted
                // or has just been canceled; capture any exception for logging.  This is
                // considered non-fatal, as a new instance of the task will be started.

                if (_producerManagementTask != null)
                {
                    try
                    {
                        await _producerManagementTask.ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Logger.BufferedProducerManagementTaskError(Identifier, EventHubName, ex.Message);
                    }
                }

                // Start the background processing task responsible for ensuring updated state and managing partition
                // processing health.

                _producerManagementTask = RunProducerManagementAsync(_backgroundTasksCancellationSource.Token);
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException opEx)
                {
                    throw new TaskCanceledException(opEx.Message, opEx);
                }

                Logger.BufferedProducerBackgroundProcessingStartError(Identifier, EventHubName, ex.Message);
                throw;
            }
            finally
            {
                Logger.BufferedProducerBackgroundProcessingStartComplete(Identifier, EventHubName);
            }
        }

        /// <summary>
        ///   Performs the actions needed to stop processing events. the <see cref="EventHubBufferedProducerClient" /> to begin accepting events to be
        ///   enqueued and published.  Should this method be called while processing is not active, no action is taken.
        /// </summary>
        ///
        /// <param name="cancelActiveSendOperations"><c>true</c> if active "SendAsync" operations should be canceled; otherwise, <c>false</c>.</param>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> instance to signal the request to cancel the stop operation.</param>
        ///
        /// <remarks>
        ///   This method will modify class-level state and should be synchronized.  It is assumed that callers hold responsibility for
        ///   ensuring synchronization concerns; this method should be invoked after any primitives have been acquired.  Callers are also
        ///   assumed to own responsibility for any validation that the client is in the intended state before calling.
        ///
        ///   This method does not consider any events that have been enqueued and are in a pending state.  It is assumed
        ///   that the caller has responsibility for disposing of any active partition state and invoking <see cref="FlushInternalAsync" />
        ///   or <see cref="ClearInternalAsync" /> as needed.
        /// </remarks>
        ///
        private async Task StopPublishingAsync(bool cancelActiveSendOperations,
                                               CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested<TaskCanceledException>();
            Logger.BufferedProducerBackgroundProcessingStop(Identifier, EventHubName);

            var capturedExceptions = default(List<Exception>);

            try
            {
                // If there is no task running for the background management process, there
                // is nothing to stop.

                if (_producerManagementTask == null)
                {
                    return;
                }

                // Request cancellation of the background processing.

                _backgroundTasksCancellationSource?.Cancel();
                _backgroundTasksCancellationSource?.Dispose();

                if (cancelActiveSendOperations)
                {
                    _activeSendOperationsCancellationSource?.Cancel();
                }

                // Wait for the background tasks to complete.

                try
                {
                    await _producerManagementTask.ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    // This is expected; no action is needed.
                }
                catch (Exception ex)
                {
                    Logger.BufferedProducerBackgroundProcessingStopError(Identifier, EventHubName, ex.Message);
                    (capturedExceptions ??= new List<Exception>()).Add(ex);
                }

                _producerManagementTask = null;
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException opEx)
                {
                    throw new TaskCanceledException(opEx.Message, opEx);
                }

                Logger.BufferedProducerBackgroundProcessingStopError(Identifier, EventHubName, ex.Message);
                (capturedExceptions ??= new List<Exception>()).Add(ex);
            }
            finally
            {
                _activeSendOperationsCancellationSource?.Dispose();
                Logger.BufferedProducerBackgroundProcessingStopComplete(Identifier, EventHubName);
            }

            // Surface any exceptions that were captured during cleanup.

            if (capturedExceptions?.Count == 1)
            {
                ExceptionDispatchInfo.Capture(capturedExceptions[0]).Throw();
            }
            else if (capturedExceptions is not null)
            {
                throw new AggregateException(capturedExceptions);
            };
        }

        /// <summary>
        ///   Responsible for invoking <see cref="OnSendSucceededAsync" /> as an background task.
        /// </summary>
        ///
        /// <param name="events">The set of events belonging to the batch that was successfully published.</param>
        /// <param name="partitionId">The identifier of the partition that the batch of events was published to.</param>
        ///
        private Task InvokeOnSendSucceededAsync(IReadOnlyList<EventData> events,
                                                string partitionId) =>
            Task
                .Run(() => OnSendSucceededAsync(events, partitionId), CancellationToken.None)
                .ContinueWith(task => task.Exception.Handle(ex => true), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

        /// <summary>
        ///   Responsible for invoking <see cref="OnSendFailedAsync" /> as an background task.
        /// </summary>
        ///
        /// <param name="events">The set of events belonging to the batch that failed to be published.</param>
        /// <param name="exception">The <see cref="Exception"/> that was raised when the events failed to publish.</param>
        /// <param name="partitionId">The identifier of the partition that the batch of events was published to.</param>
        ///
        private Task InvokeOnSendFailedAsync(IReadOnlyList<EventData> events,
                                             Exception exception,
                                             string partitionId) =>
            Task
                .Run(() => OnSendFailedAsync(events, exception, partitionId), CancellationToken.None)
                .ContinueWith(task => task.Exception.Handle(ex => true), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

        /// <summary>
        ///   Assigns a partition for a single event using a round-robin approach.
        /// </summary>
        ///
        /// <returns>The identifier of the partition assigned.</returns>
        ///
        private string AssignPartition()
        {
            // ======================================================================
            //  NOTE:
            //    This method is currently just a stub; full functionality will be
            //    added in a later set of changes.
            // ======================================================================

            //TODO: Implement AssignPartition
            return _partitions[0];
        }

        /// <summary>
        ///   Assigns a partition for a partition key, using a stable hash calculation.
        /// </summary>
        ///
        /// <param name="partitionKey">The partition key to assign a partition for.</param>
        ///
        /// <returns>The identifier of the partition assigned.</returns>
        ///
        private string AssignPartitionForPartitionKey(string partitionKey)
        {
            // ======================================================================
            //  NOTE:
            //    This method is currently just a stub; full functionality will be
            //    added in a later set of changes.
            // ======================================================================

            //TODO: Implement AssignPartitionForPartitionKey
            return partitionKey.Length >= 0 ? _partitions[0] : _partitions[0];
        }

        /// <summary>
        ///   Performs the tasks needed to manage state for the <see cref="EventHubBufferedProducerClient" /> and
        ///   ensure the health of the tasks responsible for partition processing.
        /// </summary>
        ///
        /// <param name="cancellationToken">A <see cref="CancellationToken" /> instance to signal the request to cancel the operation.</param>
        ///
        private async Task RunProducerManagementAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Logger.BufferedProducerManagementCycleStart(Identifier, EventHubName);
                var cycleDuration = ValueStopwatch.StartNew();

                try
                {
                    // Refresh the partition information.

                    var currentPartitions = await _producer.GetPartitionIdsAsync(cancellationToken).ConfigureAwait(false);

                    // Assume that if the count of partitions matches the current count, then no updated is needed.
                    // This is safe because partition identifiers are stable and new partitions can be added but
                    // the can never be removed.

                    if ((_partitions?.Length ?? 0) != currentPartitions.Length)
                    {
                        // The partitions need to be updated.  Because the two class members tracking partitions
                        // are used for different purposes, it is permissible for them to drift for a short time.
                        // As a result, there's no need to synchronize them.

                        var currentHash = new HashSet<string>(currentPartitions);

                        _partitions = currentPartitions;
                        _partitionHash = currentHash;
                    }

                    // Ensure that the publishing task is running.

                    if (_publishingTask == null)
                    {
                        _publishingTask = RunPublishingAsync(cancellationToken);
                        Logger.BufferedProducerPublishingTaskInitialStart(Identifier, EventHubName);
                    }
                    else if (_publishingTask.IsCompleted)
                    {
                        try
                        {
                            await _publishingTask.ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not TaskCanceledException)
                        {
                            Logger.BufferedProducerPublishingTaskError(Identifier, EventHubName, ex.Message);
                        }

                        _publishingTask = RunPublishingAsync(cancellationToken);
                        Logger.BufferedProducerPublishingTaskRestart(Identifier, EventHubName);
                    }
                }
                catch (OperationCanceledException)
                {
                    // This is expected; allow the loop to continue and exit normally
                    // so that the publishing task can be shut down.
                }
                catch (Exception ex)
                {
                    // Exceptions in the management operations are not critical; log but
                    // allow the loop to continue.

                    Logger.BufferedProducerManagementTaskError(Identifier, EventHubName, ex.Message);
                }

                // Determine the delay to apply before the next management cycle; if cancellation
                // was requested, then do not delay and allow the loop to terminate.

                var remainingTimeUntilNextCycle = BackgroundManagementInterval.CalculateRemaining(cycleDuration.GetElapsedTime());
                Logger.BufferedProducerManagementCycleComplete(Identifier, EventHubName, _partitions.Length, cycleDuration.GetElapsedTime().TotalSeconds, remainingTimeUntilNextCycle.TotalSeconds);

                if ((!cancellationToken.IsCancellationRequested) && (ShouldWait(remainingTimeUntilNextCycle, TimeSpan.Zero)))
                {
                    try
                    {
                        await Task.Delay(remainingTimeUntilNextCycle, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected; take no action and allow the loop to exit so shutdown
                        // can continue.
                    }
                }
            }

            // If there was a publishing task, it has the same cancellation token associated.
            // Wait for it to complete; allow any exceptions to surface.  In the normal case, this
            // will be a cancellation exception; otherwise, there was an actual error.

            if (_publishingTask != null)
            {
                try
                {
                    await _publishingTask.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not TaskCanceledException)
                {
                    Logger.BufferedProducerPublishingTaskError(Identifier, EventHubName, ex.Message);
                    throw;
                }
            }

            throw new TaskCanceledException();
        }

        /// <summary>
        ///   Performs the tasks needed to perform publishing for the <see cref="EventHubBufferedProducerClient" />.
        /// </summary>
        ///
        /// <param name="cancellationToken">A <see cref="CancellationToken" /> instance to signal the request to cancel the operation.</param>
        ///
        private Task RunPublishingAsync(CancellationToken cancellationToken) =>
            Task.Run(async () =>
            {
                var operationId = GenerateOperationId();
                Logger.BufferedProducerPublishingManagementStart(Identifier, EventHubName, operationId);

                try
                {
                    // There should be only one instance of this background publishing task running, so it is safe to assume
                    // no other publishing operations are active.  Reset the operation cancellation source to ensure that
                    // any prior cancellation or disposal does not prevent cancelling operations created here.

                    var activeOperationCancellationSource = new CancellationTokenSource();
                    var existingSource = Interlocked.Exchange(ref _activeSendOperationsCancellationSource, activeOperationCancellationSource);
                    existingSource?.Dispose();

                    var partitionIndex = 0;
                    var activeTasks = new List<Task>(_options.MaximumConcurrentSends);

                    while (!cancellationToken.IsCancellationRequested)
                    {
                        // If needed, wait for publishing tasks to complete so there is room.

                        while (activeTasks.Count >= _options.MaximumConcurrentSends)
                        {
                            var awaiSingleWatch = ValueStopwatch.StartNew();
                            Logger.BufferedProducerPublishingAwaitStart(Identifier, EventHubName, activeTasks.Count, operationId);

                            // The publishing task is responsible for managing its own exceptions and will not throw.

                            var finished = await Task.WhenAny(activeTasks).ConfigureAwait(false);
                            activeTasks.Remove(finished);

                            Logger.BufferedProducerPublishingAwaitComplete(Identifier, EventHubName, activeTasks.Count, operationId, awaiSingleWatch.GetElapsedTime().TotalSeconds);
                        }

                        // Select a partition to process.

                        var partition = _partitions[partitionIndex];
                        partitionIndex = IncrementAndRollover(partitionIndex, _partitions.Length - 1);

                        // If the selected partition is not actively being used to enqueue or its semaphore cannot be
                        // acquired within the time limit, the partition is not available and the loop should iterate
                        // to consider the next available partition.
                        //
                        // There is a benign race condition in checking the buffered event count, resulting in a partition
                        // that has just had an event enqueued to be skipped for a cycle, but this is permissible to favor
                        // building dense batches.
                        //
                        // The semaphore guard is intentionally acquired as the last clause to ensure that it is only attempted
                        // if all other conditions are true; this ensures that it does not need to be released if checks fail
                        // and a batch isn't going to be published for this iteration.

                        if ((!cancellationToken.IsCancellationRequested)
                            && (_activePartitionPublishingStateMap.TryGetValue(partition, out var partitionState))
                            && (partitionState.BufferedEventCount > 0)
                            && (partitionState.PartitionGuard.Wait(PartitionPublishingGuardAcquireLimitMilliseconds, cancellationToken)))
                        {
                            // Responsibility for releasing the guard semaphore is passed to the task.

                            activeTasks.Add(PublishBatchToPartition(partitionState, releaseGuard: true, activeOperationCancellationSource.Token));
                        }

                        // If there are no publishing tasks active, introduce a small
                        // delay to avoid a tight loop.

                        if (activeTasks.Count == 0)
                        {
                            try
                            {
                                await Task.Delay(DefaultPublishingDelayInterval, cancellationToken).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                // This is expected; allow the loop to exit with no further interaction.
                            }
                        }
                    }

                    // Wait for all of the active publishing tasks to complete; these hold responsibility for
                    // managing and logging their exceptions and should never throw.  The tasks also will honor
                    // cancellation internally and should be fully awaited rather than allowing "WhenAll" to cancel.

                    Logger.BufferedProducerPublishingAwaitAllStart(Identifier, EventHubName, activeTasks.Count, operationId);

                    var awaitAllWatch = ValueStopwatch.StartNew();
                    await Task.WhenAll(activeTasks).ConfigureAwait(false);

                    Logger.BufferedProducerPublishingAwaitAllComplete(Identifier, EventHubName, activeTasks.Count, operationId, awaitAllWatch.GetElapsedTime().TotalSeconds);
                }
                catch (Exception ex)
                {
                    // The publishing tasks are responsible for managing their own exceptions and should not
                    // throw.  If we reach this block, then it indicates something is not working as expected
                    // and this task should fail.

                    Logger.BufferedProducerPublishingManagementError(Identifier, EventHubName, operationId, ex.Message);
                    throw;
                }
                finally
                {
                    Logger.BufferedProducerPublishingManagementComplete(Identifier, EventHubName, operationId);
                }
            }, cancellationToken);

        /// <summary>
        ///   Ensures that no more than a single partition reference is active.
        /// </summary>
        ///
        /// <param name="partitionId">The identifier of the partition to which the producer is bound.</param>
        /// <param name="partitionKey">The hash key for partition routing that was requested for a publish operation.</param>
        ///
        private static void AssertSinglePartitionReference(string partitionId,
                                                           string partitionKey)
        {
            if ((!string.IsNullOrEmpty(partitionId)) && (!string.IsNullOrEmpty(partitionKey)))
            {
                throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, Resources.CannotSendWithPartitionIdAndPartitionKey, partitionKey, partitionId));
            }
        }

        /// <summary>
        ///   Ensures that if a partition identifier was specified, it refers to a valid
        ///   partition for the Event Hub.
        /// </summary>
        ///
        /// <param name="partitionId">The identifier of the partition to which the producer is bound.</param>
        /// <param name="validPartitions">The set of valid partitions to consider.</param>
        ///
        private static void AssertValidPartition(string partitionId,
                                                 HashSet<string> validPartitions)
        {
            if (!validPartitions.Contains(partitionId))
            {
                throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, Resources.CannotSendToUknownPartition, partitionId));
            }
        }

        /// <summary>
        ///   Ensures that an event handler required for enqueuing events has been set.
        /// </summary>
        ///
        /// <param name="handler">The event handler instance to validate.</param>
        /// <param name="handlerName">The name of the event handler.</param>
        ///
        private static void AssertRequiredHandlerSetForEnqueue(object handler,
                                                               string handlerName)
        {
            if (handler == null)
            {
                throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, Resources.CannotEnqueueEventWithoutHandler, handlerName));
            }
        }

        /// <summary>
        ///   Creates a channel for queuing events to be published.
        /// </summary>
        ///
        /// <param name="capacity">The maximum amount of events that can be queued in the channel.</param>
        ///
        /// <returns>A bounded channel, configured for many:many read/write usage.</returns>
        ///
        private static Channel<EventData> CreatePendingEventChannel(int capacity) =>
            Channel.CreateBounded<EventData>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = false,
                SingleReader = false
            });

        /// <summary>
        ///   Generates a unique identifier to be used for correlation in a
        ///   logging scope.
        /// </summary>
        ///
        /// <returns>The identifier that was generated.</returns>
        ///
        private static string GenerateOperationId() => Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);

        /// <summary>
        ///   Increments a value and rolls over to the minimum value if it exceeds the
        ///   maximum.
        /// </summary>
        ///
        /// <param name="value">The value to increment.</param>
        /// <param name="maximum">The maximum (inclusive) that the value can reach before rolling over.</param>
        /// <param name="minimum">The minimum that the <paramref name="value" /> should roll over to, if it exceeds the <paramref name="maximum" />.</param>
        ///
        /// <returns>The incremented <paramref name="value" /> with rollover applied.</returns>
        ///
        private static int IncrementAndRollover(int value,
                                                int maximum,
                                                int minimum = 0) => (++value > maximum) ? minimum : value;

        /// <summary>
        ///   Determines if waiting should take place, taking into account <see cref="Timeout.InfiniteTimeSpan" />
        ///   as an indicator of a desire to always wait.
        /// </summary>
        ///
        /// <param name="waitTime">The desired interval to wait.</param>
        /// <param name="minimumAllowedWaitTime">The minimum allowed interval for waiting.</param>
        ///
        /// <returns><c>true</c> if waiting should be allowed; otherwise, <c>false</c>.</returns>
        ///
        private static bool ShouldWait(TimeSpan waitTime,
                                       TimeSpan minimumAllowedWaitTime) =>
          ((waitTime == Timeout.InfiniteTimeSpan) || (waitTime > minimumAllowedWaitTime));

        /// <summary>
        ///   Calculates the amount of delay to apply, ensuring that the remaining time allotted
        ///   supersedes the delay amount, if not enough time remains for the full delay.
        /// </summary>
        /// <param name="remainingTime">The amount of allotted time remaining.</param>
        /// <param name="delayInterval">The desired delay interval.</param>
        ///
        /// <returns>The amount of delay to apply.</returns>
        ///
        private static TimeSpan CalculateDelay(TimeSpan remainingTime,
                                               TimeSpan delayInterval) => ((remainingTime != Timeout.InfiniteTimeSpan) && (remainingTime < delayInterval)) ? remainingTime : delayInterval;

        /// <summary>
        ///   The set of information needed to track and manage the active publishing
        ///   activities for a partition.
        /// </summary>
        ///
        internal class PartitionPublishingState : IDisposable
        {
            /// <summary>The writer to use for enqueuing events to be published.</summary>
            public ChannelWriter<EventData> PendingEventsWriter => _pendingEvents.Writer;

            /// <summary>The identifier of the partition that is being published.</summary>
            public readonly string PartitionId;

            /// <summary>The primitive for synchronizing access for publishing to the partition.</summary>
            public readonly SemaphoreSlim PartitionGuard;

            /// <summary>The number of events that are currently buffered and waiting to be published for this partition.</summary>
            public int BufferedEventCount;

            /// <summary>An event that has been stashed to be read as the next available event; this is necessary due to the inability to peek the channel.</summary>
            private ConcurrentQueue<EventData> _stashedEvents;

            /// <summary>The events that have been enqueued and are pending publishing.</summary>
            private readonly Channel<EventData> _pendingEvents;

            /// <summary>
            ///   Initializes a new instance of the <see cref="PartitionPublishingState"/> class.
            /// </summary>
            ///
            /// <param name="partitionId">The identifier of the partition this publisher is associated with.</param>
            /// <param name="options">The options used for creating the buffered producer.</param>
            ///
            public PartitionPublishingState(string partitionId,
                                            EventHubBufferedProducerClientOptions options)
            {
                PartitionId = partitionId;
                PartitionGuard = new(options.MaximumConcurrentSendsPerPartition, options.MaximumConcurrentSendsPerPartition);

                _pendingEvents = CreatePendingEventChannel(options.MaximumEventBufferLengthPerPartition);
                _stashedEvents = new();
            }

            /// <summary>
            ///   Attempts to read an event to be published.
            /// </summary>
            ///
            /// <param name="readEvent">The event, if one was read; otherwise, <c>null</c>.</param>
            ///
            /// <returns><c>true</c> if an event was read; otherwise, <c>false</c>.</returns>
            ///
            public bool TryReadEvent(out EventData readEvent)
            {
                if (_stashedEvents.TryDequeue(out readEvent))
                {
                    return true;
                }

                return _pendingEvents.Reader.TryRead(out readEvent);
            }

            /// <summary>
            ///   Stashes an event with priority for the next time a read is requested.  This is
            ///   intended to both support concurrent publishing for a partition and to work around
            ///   the lack of "Peek" operation for channels.
            /// </summary>
            ///
            /// <param name="eventData">The event to stash.</param>
            ///
            public void StashEvent(EventData eventData) => _stashedEvents.Enqueue(eventData);

            /// <summary>
            ///   Performs tasks needed to clean-up the disposable resources used by the publisher.
            /// </summary>
            ///
            public void Dispose()
            {
                _pendingEvents.Writer.TryComplete();
                PartitionGuard.Dispose();
            }
        }
    }
}
