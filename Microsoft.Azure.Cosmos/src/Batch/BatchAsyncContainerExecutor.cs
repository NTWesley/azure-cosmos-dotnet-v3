﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos.Routing;
    using Microsoft.Azure.Documents;

    /// <summary>
    /// Bulk batch executor for operations in the same container.
    /// </summary>
    /// <remarks>
    /// It maintains one <see cref="BatchAsyncStreamer"/> for each Partition Key Range, which allows independent execution of requests.
    /// Semaphores are in place to rate limit the operations at the Streamer / Partition Key Range level, this means that we can send parallel and independent requests to different Partition Key Ranges, but for the same Range, requests will be limited.
    /// Two delegate implementations define how a particular request should be executed, and how operations should be retried. When the <see cref="BatchAsyncStreamer"/> dispatches a batch, the batch will create a request and call the execute delegate, if conditions are met, it might call the retry delegate.
    /// </remarks>
    /// <seealso cref="BatchAsyncStreamer"/>
    internal class BatchAsyncContainerExecutor : IDisposable
    {
        private const int DefaultDispatchTimerInSeconds = 1;
        private const int MinimumDispatchTimerInSeconds = 1;

        private readonly ContainerCore cosmosContainer;
        private readonly CosmosClientContext cosmosClientContext;
        private readonly int maxServerRequestBodyLength;
        private readonly int maxServerRequestOperationCount;
        private readonly int dispatchTimerInSeconds;
        private readonly ConcurrentDictionary<string, BatchAsyncStreamer> streamersByPartitionKeyRange = new ConcurrentDictionary<string, BatchAsyncStreamer>();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> limitersByPartitionkeyRange = new ConcurrentDictionary<string, SemaphoreSlim>();
        private readonly TimerPool timerPool;
        private readonly RetryOptions retryOptions;

        private readonly ConcurrentBag<(int, bool, double, double, double)> countsAndLatencies = new ConcurrentBag<(int, bool, double, double, double)>();
        private readonly Stopwatch stopwatch = new Stopwatch();
        private readonly ConcurrentDictionary<string, int> docsPartitionId = new ConcurrentDictionary<string, int>();
        private readonly ConcurrentDictionary<string, int> throttlePartitionId = new ConcurrentDictionary<string, int>();
        private readonly ConcurrentDictionary<string, long> timePartitionid = new ConcurrentDictionary<string, long>();
        private bool disposeCongestionController = false;

        /// <summary>
        /// For unit testing.
        /// </summary>
        internal BatchAsyncContainerExecutor()
        {
        }

        public BatchAsyncContainerExecutor(
            ContainerCore cosmosContainer,
            CosmosClientContext cosmosClientContext,
            int maxServerRequestOperationCount,
            int maxServerRequestBodyLength,
            int dispatchTimerInSeconds = BatchAsyncContainerExecutor.DefaultDispatchTimerInSeconds)
        {
            if (cosmosContainer == null)
            {
                throw new ArgumentNullException(nameof(cosmosContainer));
            }

            if (maxServerRequestOperationCount < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxServerRequestOperationCount));
            }

            if (maxServerRequestBodyLength < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxServerRequestBodyLength));
            }

            if (dispatchTimerInSeconds < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(dispatchTimerInSeconds));
            }

            this.cosmosContainer = cosmosContainer;
            this.cosmosClientContext = cosmosClientContext;
            this.maxServerRequestBodyLength = maxServerRequestBodyLength;
            this.maxServerRequestOperationCount = maxServerRequestOperationCount;
            this.dispatchTimerInSeconds = dispatchTimerInSeconds;
            this.timerPool = new TimerPool(BatchAsyncContainerExecutor.MinimumDispatchTimerInSeconds);
            this.retryOptions = cosmosClientContext.ClientOptions.GetConnectionPolicy().RetryOptions;

            this.stopwatch.Start();
        }

        public virtual async Task<TransactionalBatchOperationResult> AddAsync(
            ItemBatchOperation operation,
            ItemRequestOptions itemRequestOptions = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            await this.ValidateOperationAsync(operation, itemRequestOptions, cancellationToken);

            string resolvedPartitionKeyRangeId = await this.ResolvePartitionKeyRangeIdAsync(operation, cancellationToken).ConfigureAwait(false);
            BatchAsyncStreamer streamer = this.GetOrAddStreamerForPartitionKeyRange(resolvedPartitionKeyRangeId);
            ItemBatchOperationContext context = new ItemBatchOperationContext(resolvedPartitionKeyRangeId, BatchAsyncContainerExecutor.GetRetryPolicy(this.retryOptions));
            operation.AttachContext(context);
            streamer.Add(operation);
            return await context.OperationTask;
        }

        public void Dispose()
        {
            this.disposeCongestionController = true;
            foreach (KeyValuePair<string, BatchAsyncStreamer> streamer in this.streamersByPartitionKeyRange)
            {
                streamer.Value.Dispose();
            }

            foreach (KeyValuePair<string, SemaphoreSlim> limiter in this.limitersByPartitionkeyRange)
            {
                limiter.Value.Dispose();
            }

            this.timerPool.Dispose();

            IEnumerable<IGrouping<int, (int, bool, double, double, double)>> gs = this.countsAndLatencies.GroupBy(i => i.Item1);
            double totalCharge = 0;
            double totalTime = 0;
            foreach (IGrouping<int, (int, bool, double, double, double)> g in gs)
            {
                Console.WriteLine($"BatchItemCount: {g.Key} BatchCount: {g.Count()} RateLimitedBatches: {g.Count(h => h.Item2)} AvgLatencyInMs: {g.Average(h => h.Item4)} AvgBackendLatencyMs: {g.Average(h => h.Item5)}");
                totalCharge += g.Sum(h => h.Item3);
                totalTime += g.Sum(h => h.Item4);

            }

            Console.WriteLine($"TotalCharge: {totalCharge} and total time: {totalTime}");
        }

        internal virtual async Task ValidateOperationAsync(
            ItemBatchOperation operation,
            ItemRequestOptions itemRequestOptions = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (itemRequestOptions != null)
            {
                if (itemRequestOptions.BaseConsistencyLevel.HasValue
                                || itemRequestOptions.PreTriggers != null
                                || itemRequestOptions.PostTriggers != null
                                || itemRequestOptions.SessionToken != null)
                {
                    throw new InvalidOperationException(ClientResources.UnsupportedBulkRequestOptions);
                }

                Debug.Assert(BatchAsyncContainerExecutor.ValidateOperationEPK(operation, itemRequestOptions));
            }

            await operation.MaterializeResourceAsync(this.cosmosClientContext.CosmosSerializer, cancellationToken);
        }

        private static IDocumentClientRetryPolicy GetRetryPolicy(RetryOptions retryOptions)
        {
            return new BulkPartitionKeyRangeGoneRetryPolicy(
                new ResourceThrottleRetryPolicy(
                retryOptions.MaxRetryAttemptsOnThrottledRequests,
                retryOptions.MaxRetryWaitTimeInSeconds));
        }

        private static bool ValidateOperationEPK(
            ItemBatchOperation operation,
            ItemRequestOptions itemRequestOptions)
        {
            if (itemRequestOptions.Properties != null
                            && (itemRequestOptions.Properties.TryGetValue(WFConstants.BackendHeaders.EffectivePartitionKey, out object epkObj)
                            | itemRequestOptions.Properties.TryGetValue(WFConstants.BackendHeaders.EffectivePartitionKeyString, out object epkStrObj)))
            {
                byte[] epk = epkObj as byte[];
                string epkStr = epkStrObj as string;
                if (epk == null || epkStr == null)
                {
                    throw new InvalidOperationException(string.Format(
                        ClientResources.EpkPropertiesPairingExpected,
                        WFConstants.BackendHeaders.EffectivePartitionKey,
                        WFConstants.BackendHeaders.EffectivePartitionKeyString));
                }

                if (operation.PartitionKey != null)
                {
                    throw new InvalidOperationException(ClientResources.PKAndEpkSetTogether);
                }
            }

            return true;
        }

        private static void AddHeadersToRequestMessage(RequestMessage requestMessage, string partitionKeyRangeId)
        {
            requestMessage.Headers.PartitionKeyRangeId = partitionKeyRangeId;
            requestMessage.Headers.Add(HttpConstants.HttpHeaders.ShouldBatchContinueOnError, bool.TrueString);
            requestMessage.Headers.Add(HttpConstants.HttpHeaders.IsBatchRequest, bool.TrueString);
        }

        private async Task ReBatchAsync(
            ItemBatchOperation operation,
            CancellationToken cancellationToken)
        {
            string resolvedPartitionKeyRangeId = await this.ResolvePartitionKeyRangeIdAsync(operation, cancellationToken).ConfigureAwait(false);
            BatchAsyncStreamer streamer = this.GetOrAddStreamerForPartitionKeyRange(resolvedPartitionKeyRangeId);
            streamer.Add(operation);
        }

        private async Task<string> ResolvePartitionKeyRangeIdAsync(
            ItemBatchOperation operation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PartitionKeyDefinition partitionKeyDefinition = await this.cosmosContainer.GetPartitionKeyDefinitionAsync(cancellationToken);
            CollectionRoutingMap collectionRoutingMap = await this.cosmosContainer.GetRoutingMapAsync(cancellationToken);

            Debug.Assert(operation.RequestOptions?.Properties?.TryGetValue(WFConstants.BackendHeaders.EffectivePartitionKeyString, out object epkObj) == null, "EPK is not supported");
            await this.FillOperationPropertiesAsync(operation, cancellationToken);
            return BatchExecUtils.GetPartitionKeyRangeId(operation.PartitionKey.Value, partitionKeyDefinition, collectionRoutingMap);
        }

        private async Task FillOperationPropertiesAsync(ItemBatchOperation operation, CancellationToken cancellationToken)
        {
            // Same logic from RequestInvokerHandler to manage partition key migration
            if (object.ReferenceEquals(operation.PartitionKey, PartitionKey.None))
            {
                Documents.Routing.PartitionKeyInternal partitionKeyInternal = await this.cosmosContainer.GetNonePartitionKeyValueAsync(cancellationToken).ConfigureAwait(false);
                operation.PartitionKeyJson = partitionKeyInternal.ToJsonString();
            }
            else
            {
                operation.PartitionKeyJson = operation.PartitionKey.Value.ToString();
            }
        }

        private async Task<PartitionKeyRangeBatchExecutionResult> ExecuteAsync(
            PartitionKeyRangeServerBatchRequest serverRequest,
            CancellationToken cancellationToken)
        {
            SemaphoreSlim limiter = this.GetOrAddLimiterForPartitionKeyRange(serverRequest.PartitionKeyRangeId, cancellationToken);
            int numThrottle = -1;
            PartitionKeyRangeBatchExecutionResult result = null;
            await limiter.WaitAsync(cancellationToken);

            try
            {
                using (Stream serverRequestPayload = serverRequest.TransferBodyStream())
                {
                    Debug.Assert(serverRequestPayload != null, "Server request payload expected to be non-null");

                    TimeSpan start = this.stopwatch.Elapsed;
                    ResponseMessage responseMessage = await this.cosmosClientContext.ProcessResourceOperationStreamAsync(
                        this.cosmosContainer.LinkUri,
                        ResourceType.Document,
                        OperationType.Batch,
                        new RequestOptions(),
                        cosmosContainerCore: this.cosmosContainer,
                        partitionKey: null,
                        streamPayload: serverRequestPayload,
                        requestEnricher: requestMessage => BatchAsyncContainerExecutor.AddHeadersToRequestMessage(requestMessage, serverRequest.PartitionKeyRangeId),
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    await Task.CompletedTask.ConfigureAwait(false);

                    double backendElapsedTime = (this.stopwatch.Elapsed - start).TotalMilliseconds;

                    TransactionalBatchResponse serverResponse = await
                    TransactionalBatchResponse.FromResponseMessageAsync(responseMessage, serverRequest, this.cosmosClientContext.CosmosSerializer, false).ConfigureAwait(false);

                    numThrottle = serverResponse.Count(r => r.StatusCode == (System.Net.HttpStatusCode)429);
                    long secondsElapsed = (this.stopwatch.Elapsed - start).Seconds;
                    this.throttlePartitionId.AddOrUpdate(serverRequest.PartitionKeyRangeId, numThrottle, (_, old) => old + numThrottle);
                    this.docsPartitionId.AddOrUpdate(serverRequest.PartitionKeyRangeId, serverResponse.Count, (_, old) => old + serverResponse.Count);
                    this.timePartitionid.AddOrUpdate(serverRequest.PartitionKeyRangeId, secondsElapsed, (_, old) => old + secondsElapsed);

                    this.countsAndLatencies.Add(
                        (serverRequest.Operations.Count,
                        serverResponse.Any(r => r.StatusCode == (System.Net.HttpStatusCode)429),
                        serverResponse.RequestCharge,
                        (this.stopwatch.Elapsed - start).TotalMilliseconds,
                        backendElapsedTime));
                    result = new PartitionKeyRangeBatchExecutionResult(serverRequest.PartitionKeyRangeId, serverRequest.Operations, serverResponse);
                }
            }
            catch (Exception ex)
            {
                Trace.TraceError(ex.Message);
                throw ex;
            }
            finally
            {
                // No matter what, when the task is finished the semaphore is released letting the next task run.
                limiter.Release();
            }

            return result;
        }

        private BatchAsyncStreamer GetOrAddStreamerForPartitionKeyRange(string partitionKeyRangeId)
        {
            if (this.streamersByPartitionKeyRange.TryGetValue(partitionKeyRangeId, out BatchAsyncStreamer streamer))
            {
                return streamer;
            }

            BatchAsyncStreamer newStreamer = new BatchAsyncStreamer(this.maxServerRequestOperationCount, this.maxServerRequestBodyLength, this.dispatchTimerInSeconds, this.timerPool, this.cosmosClientContext.CosmosSerializer, this.ExecuteAsync, this.ReBatchAsync);
            if (!this.streamersByPartitionKeyRange.TryAdd(partitionKeyRangeId, newStreamer))
            {
                newStreamer.Dispose();
            }

            return this.streamersByPartitionKeyRange[partitionKeyRangeId];
        }

        private SemaphoreSlim GetOrAddLimiterForPartitionKeyRange(string partitionKeyRangeId, CancellationToken cancellationToken)
        {
            if (this.limitersByPartitionkeyRange.TryGetValue(partitionKeyRangeId, out SemaphoreSlim limiter))
            {
                return limiter;
            }

            SemaphoreSlim newLimiter = new SemaphoreSlim(5, int.MaxValue);
            if (!this.limitersByPartitionkeyRange.TryAdd(partitionKeyRangeId, newLimiter))
            {
                newLimiter.Dispose();
            }
            else
            {
                // Addition sucessfull meaning a new limiter was created, so create congestion control on top of it
                this.CongestionControlTask(partitionKeyRangeId, newLimiter, cancellationToken);
            }

            return this.limitersByPartitionkeyRange[partitionKeyRangeId];
        }

        private void CongestionControlTask(string partitionKeyRangeId, SemaphoreSlim limiter, CancellationToken cancellationToken)
        {
            Task congestionControllerTask = Task.Run(async () =>
            {
                long waitTimeForLoggingMetrics = 1;
                long lastElapsedTime = 0;

                int startingDegreeOfConcurrency = 5;
                int defaultMaxDegreeOfConcurrency = 60;
                int additiveIncreaseFactor = 5;
                double multiplicativeDecreaseFactor = 1.0;
                int degreeOfConcurrency = startingDegreeOfConcurrency;
                int maxDegreeOfConcurrency = defaultMaxDegreeOfConcurrency;

                int oldThrttleCount = 0;
                int oldDocCount = 0;

                while (!this.disposeCongestionController)
                {
                    this.timePartitionid.TryGetValue(partitionKeyRangeId, out long currentElapsedTime);
                    long elapsedTime = currentElapsedTime - lastElapsedTime;
                    lastElapsedTime = currentElapsedTime;

                    if (elapsedTime >= waitTimeForLoggingMetrics)
                    {
                        waitTimeForLoggingMetrics += 1;

                        this.docsPartitionId.TryGetValue(partitionKeyRangeId, out int newDocCount);
                        this.throttlePartitionId.TryGetValue(partitionKeyRangeId, out int newThrottleCount);

                        int diffThrottle = newThrottleCount - oldThrttleCount;
                        oldThrttleCount = newThrottleCount;

                        int changeDocCount = newDocCount - oldDocCount;
                        oldDocCount = newDocCount;

                        if (diffThrottle > 0)
                        {
                            additiveIncreaseFactor = 1;
                            double decreaseFactor = multiplicativeDecreaseFactor + (1000.0 / ((diffThrottle < 1000 ? 1000 : diffThrottle) * 1.0));

                            int decreaseCount = (int)(degreeOfConcurrency * 1.0 / decreaseFactor);

                            // We got a throttle so we need to back off on the degree of concurrency.
                            // Get the current degree of concurrency and decrease that (AIMD).
                            for (int i = 0; i < decreaseCount; i++)
                            {
                                await limiter.WaitAsync(cancellationToken);
                            }
                            degreeOfConcurrency -= decreaseCount;
                        }

                        if (changeDocCount > 0 && diffThrottle == 0)
                        {
                            if (degreeOfConcurrency + additiveIncreaseFactor <= maxDegreeOfConcurrency)
                            {
                                // We aren't getting throttles, so we should bump of the degree of concurrency (AIAD).
                                limiter.Release(additiveIncreaseFactor);
                                degreeOfConcurrency = degreeOfConcurrency + additiveIncreaseFactor;
                            }
                        }
                    }
                    else
                    {
                        await Task.Delay(2);
                    }
                }
            }, cancellationToken);
        }
    }
}
