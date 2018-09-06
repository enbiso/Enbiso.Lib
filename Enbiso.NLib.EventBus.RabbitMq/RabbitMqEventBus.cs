﻿using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Polly;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace Enbiso.NLib.EventBus.RabbitMq
{
    /// <inheritdoc />
    /// <summary>
    /// Rabbit implementation of integrationEvent bus
    /// </summary>
    public class RabbitMqEventBus : IEventBus
    {
        private readonly string _brokerName;        

        private readonly IRabbitMqPersistentConnection _persistentConnection;
        private readonly ILogger<RabbitMqEventBus> _logger;
        private readonly IEventBusSubscriptionsManager _subscriptionsManager;        
        private readonly IEnumerable<IEventBusSubscriber> _subscribers;
        private readonly IServiceProvider _serviceProvider;
        private readonly int _retryCount;
        private IModel _consumerChannel;
        private readonly string _queueName;

        public RabbitMqEventBus(
            IRabbitMqPersistentConnection persistentConnection, 
            ILogger<RabbitMqEventBus> logger,            
            IEventBusSubscriptionsManager subscriptionManager,             
            IOptions<RabbitMqOption> optionWrap, 
            IEnumerable<IEventBusSubscriber> subscribers, 
            IServiceProvider serviceProvider)
        {
            var option = optionWrap.Value;
            _queueName = option.Client ?? throw new ArgumentNullException(nameof(option.Client));
            _brokerName = option.Exchange ?? throw new ArgumentNullException(nameof(option.Exchange));            
            _persistentConnection = persistentConnection ?? throw new ArgumentNullException(nameof(persistentConnection));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _subscriptionsManager = subscriptionManager;            
            _subscribers = subscribers;
            _serviceProvider = serviceProvider;
            _subscriptionsManager.OnEventRemoved += SubscriptionManager_OnEventRemoved;
            _consumerChannel = CreateConsumerChannel();
            _retryCount = option.PublishRetryCount;
        }

        /// <inheritdoc />
        public void Initialize()
        {
            _subscribers.Subscribe();

            var consumer = new EventingBasicConsumer(_consumerChannel);
            consumer.Received += async (model, ea) =>
            {
                var eventName = ea.RoutingKey;
                var message = Encoding.UTF8.GetString(ea.Body);
                await ProcessEvent(eventName, message);
            };
            _consumerChannel.BasicConsume(queue: _queueName, autoAck: false, consumer: consumer);
        }

        /// <inheritdoc />
        public void Publish(IIntegrationEvent integrationEvent)
        {
            if (!_persistentConnection.IsConnected)
            {
                _persistentConnection.TryConnect();
            }

            var policy = Policy.Handle<BrokerUnreachableException>()
                .Or<SocketException>()
                .WaitAndRetry(_retryCount, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)), (ex, time) =>
                {
                    _logger.LogWarning(ex.ToString());
                });

            using (var channel = _persistentConnection.CreateModel())
            {
                var eventName = integrationEvent.GetType().Name;
                channel.ExchangeDeclare(exchange: _brokerName, type: "direct");

                var message = JsonConvert.SerializeObject(integrationEvent);
                var body = Encoding.UTF8.GetBytes(message);
                policy.Execute(() =>
                {
                    channel.BasicPublish(exchange: _brokerName, routingKey: eventName, basicProperties: null, body: body);
                });
            }
        }

        /// <inheritdoc />
        public void Subscribe<TEvent, TEventHandler>() 
            where TEvent : IIntegrationEvent 
            where TEventHandler : IEventHandler<TEvent>
        {
            var eventName = _subscriptionsManager.GetEventKey<TEvent>();
            DoInternalSubscription(eventName);
            _subscriptionsManager.AddSubscription<TEvent, TEventHandler>();
        }

        /// <inheritdoc />
        public void Subscribe<TEvent>() where TEvent : IIntegrationEvent
        {
            Subscribe<TEvent, IEventHandler<TEvent>>();
        }

        /// <inheritdoc />
        public void SubscribeDynamic<TEventHandler>(string eventName) 
            where TEventHandler : IDynamicIntegrationEventHandler
        {
            DoInternalSubscription(eventName);
            _subscriptionsManager.AddDynamicSubscription<TEventHandler>(eventName);
        }

        /// <inheritdoc />
        public void UnsubscribeDynamic<TEventHandler>(string eventName) 
            where TEventHandler : IDynamicIntegrationEventHandler
        {
            _subscriptionsManager.RemoveDynamicSubscription<TEventHandler>(eventName);
        }

        /// <inheritdoc />
        public void Unsubscribe<TEvent, TEventHandler>() 
            where TEvent : IIntegrationEvent 
            where TEventHandler : IEventHandler<TEvent>
        {
            _subscriptionsManager.RemoveSubscription<TEvent, TEventHandler>();
        }
        
        public void Dispose()
        {
            _consumerChannel?.Dispose();
            _subscriptionsManager.Clear();
        }

        #region private methods

        private void DoInternalSubscription(string eventName)
        {
            var containsKey = _subscriptionsManager.HasSubscriptionsForEvent(eventName);
            if (containsKey) return;

            if (!_persistentConnection.IsConnected)
            {
                _persistentConnection.TryConnect();
            }

            using (var channel = _persistentConnection.CreateModel())
            {
                channel.QueueBind(queue: _queueName, exchange: _brokerName, routingKey: eventName);
            }
        }
        
        private IModel CreateConsumerChannel()
        {
            if (!_persistentConnection.IsConnected)
                _persistentConnection.TryConnect();

            var channel = _persistentConnection.CreateModel();
            channel.ExchangeDeclare(exchange: _brokerName, type: "direct");
            channel.QueueDeclare(queue: _queueName, durable: true, exclusive: false, autoDelete: false, arguments: null);

            channel.CallbackException += (sender, ea) =>
            {
                _consumerChannel.Dispose();
                _consumerChannel = CreateConsumerChannel();
            };

            return channel;
        }

        private async Task ProcessEvent(string eventName, string message)
        {            
            if (!_subscriptionsManager.HasSubscriptionsForEvent(eventName)) return;

            using (var scope = _serviceProvider.CreateScope())
            {
                var subscriptions = _subscriptionsManager.GetHandlersForEvent(eventName);
                foreach (var subscription in subscriptions)
                {
                    if (subscription.IsDynamic)
                    {
                        var handler =
                            scope.ServiceProvider.GetService(subscription.HandlerType) as IDynamicIntegrationEventHandler;
                        dynamic eventData = JObject.Parse(message);
                        if (handler != null) await handler.Handle(eventData);
                        else _logger.LogWarning($"Handler not found for {subscription.HandlerType} on {eventName}.");
                    }
                    else
                    {
                        var eventType = _subscriptionsManager.GetEventTypeByName(eventName);
                        var integrationEvent = JsonConvert.DeserializeObject(message, eventType);
                        var handler = scope.ServiceProvider.GetService(subscription.HandlerType);
                        var concreteHandlerType = typeof(IEventHandler<>).MakeGenericType(eventType);
                        await (Task)concreteHandlerType.GetMethod("Handle").Invoke(handler, new[] {integrationEvent});
                    }
                }
            }
        }

        private void SubscriptionManager_OnEventRemoved(object sender, string eventName)
        {
            if (!_persistentConnection.IsConnected)
                _persistentConnection.TryConnect();

            using (var channel = _persistentConnection.CreateModel())
            {
                channel.QueueUnbind(_queueName, _brokerName, eventName);
            }
        }

        #endregion
    }
}

