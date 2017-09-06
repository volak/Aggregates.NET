﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Aggregates.Contracts;
using Aggregates.DI;
using Aggregates.Extensions;
using Aggregates.Logging;
using App.Metrics;
using NServiceBus;
using NServiceBus.Extensibility;
using NServiceBus.Pipeline;

namespace Aggregates.Internal
{
    internal class UnitOfWorkExecutor : Behavior<IIncomingLogicalMessageContext>
    {
        private static readonly ILog Logger = LogProvider.GetLogger("UOW Executor");

        private static readonly App.Metrics.Core.Options.MeterOptions Messages =
            new App.Metrics.Core.Options.MeterOptions
            {
                Name = "Messages",
                MeasurementUnit = Unit.Items,
            };
        private static readonly App.Metrics.Core.Options.CounterOptions Concurrent =
            new App.Metrics.Core.Options.CounterOptions
            {
                Name = "Messages Concurrent",
                MeasurementUnit = Unit.Items,
            };
        private static readonly App.Metrics.Core.Options.TimerOptions Timer =
            new App.Metrics.Core.Options.TimerOptions
            {
                Name = "Message Duration",
                MeasurementUnit = Unit.Items,
            };
        private static readonly App.Metrics.Core.Options.MeterOptions Errors =
            new App.Metrics.Core.Options.MeterOptions
            {
                Name = "Message Errors",
                MeasurementUnit = Unit.Items,
            };

        private readonly IMetrics _metrics;

        public UnitOfWorkExecutor(IMetrics metrics)
        {
            _metrics = metrics;
        }

        public override async Task Invoke(IIncomingLogicalMessageContext context, Func<Task> next)
        {

            // Only SEND messages deserve a UnitOfWork
            if (context.MessageHeaders[Headers.MessageIntent] != MessageIntentEnum.Send.ToString() && context.MessageHeaders[Headers.MessageIntent] != MessageIntentEnum.Publish.ToString())
            {
                await next().ConfigureAwait(false);
                return;
            }

            var container = TinyIoCContainer.Current;

            var domainUOW = container.Resolve<IDomainUnitOfWork>();
            var appUOW = container.Resolve<IUnitOfWork>();

            // Child container with resolved domain and app uow used by downstream
            var child = container.GetChildContainer();
            child.Register(domainUOW);
            child.Register(appUOW);

            Logger.Write(LogLevel.Debug,
                () => $"Starting UOW for message {context.MessageId} type {context.Message.MessageType.FullName}");
            
            try
            {
                _metrics.Measure.Counter.Increment(Concurrent);
                _metrics.Measure.Meter.Mark(Messages);
                using (_metrics.Measure.Timer.Time(Timer))
                {
                        
                    Logger.Write(LogLevel.Debug, () => $"Running UOW.Begin for message {context.MessageId}");
                    await domainUOW.Begin().ConfigureAwait(false);
                    await appUOW.Begin().ConfigureAwait(false);


                    // Todo: because commit id is used on commit now instead of during processing we can
                    // once again parallelize event processing (if we want to)

                    // Stupid hack to get events from ES and messages from NSB into the same pipeline
                    IDelayedMessage[] delayed;
                    object @event;
                    // Special case for delayed messages read from delayed stream
                    if (context.Headers.ContainsKey(Defaults.BulkHeader) && context.Extensions.TryGet(Defaults.BulkHeader, out delayed))
                    {

                        Logger.Write(LogLevel.Debug, () => $"Bulk processing {delayed.Count()} messages, bulk id {context.MessageId}");
                        var index = 1;
                        foreach (var x in delayed)
                        {
                            // Replace all headers with the original headers to preserve CorrId etc.
                            context.Headers.Clear();
                            foreach (var header in x.Headers)
                                context.Headers[$"{Defaults.DelayedPrefixHeader}.{header.Key}"] = header.Value;

                            context.Headers[Defaults.BulkHeader] = delayed.Count().ToString();
                            context.Headers[Defaults.DelayedId] = x.MessageId;
                            context.Headers[Defaults.ChannelKey] = x.ChannelKey;
                            Logger.Write(LogLevel.Debug, () => $"Processing {index}/{delayed.Count()} message, bulk id {context.MessageId}.  MessageId: {x.MessageId} ChannelKey: {x.ChannelKey}");

                            //context.Extensions.Set(Defaults.ChannelKey, x.ChannelKey);

                            context.UpdateMessageInstance(x.Message);
                            await next().ConfigureAwait(false);
                            index++;
                        }

                    }
                    else if (context.Headers.ContainsKey(Defaults.EventHeader) &&
                             context.Extensions.TryGet(Defaults.EventHeader, out @event))
                    {

                        context.UpdateMessageInstance(@event);
                        await next().ConfigureAwait(false);
                    }
                    else
                        await next().ConfigureAwait(false);

                    
                        Logger.Write(LogLevel.Debug, () => $"Running UOW.End for message {context.MessageId}");
                        
                        await domainUOW.End().ConfigureAwait(false);
                        await appUOW.End().ConfigureAwait(false);
                    
                    

                }

            }
            catch (Exception e)
            {
                Logger.Info($"Caught exception '{e.GetType().FullName}' while executing message {context.MessageId} {context.Message.MessageType.FullName}", e);

                _metrics.Measure.Meter.Mark(Errors);
                var trailingExceptions = new List<Exception>();
                
                    try
                    {
                        Logger.Write(LogLevel.Debug,
                            () => $"Running UOW.End with exception [{e.GetType().Name}] for message {context.MessageId}");
                        await domainUOW.End(e).ConfigureAwait(false);
                        await appUOW.End(e).ConfigureAwait(false);
                }
                    catch (Exception endException)
                    {
                        trailingExceptions.Add(endException);
                    }
                

                if (trailingExceptions.Any())
                {
                    trailingExceptions.Insert(0, e);
                    throw new System.AggregateException(trailingExceptions);
                }
                throw;

            }
            finally
            {
                _metrics.Measure.Counter.Decrement(Concurrent);
            }
        }
    }
    internal class UowRegistration : RegisterStep
    {
        public UowRegistration() : base(
            stepId: "UnitOfWorkExecution",
            behavior: typeof(UnitOfWorkExecutor),
            description: "Begins and Ends unit of work for your application"
        )
        {
            InsertAfterIfExists("ExecuteUnitOfWork");
        }
    }
}

