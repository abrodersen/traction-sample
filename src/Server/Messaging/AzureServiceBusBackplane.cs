using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Management.ServiceBus;
using Microsoft.Azure.Management.ServiceBus.Models;
using Microsoft.Azure.ServiceBus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Microsoft.Rest;

namespace Server.Messaging
{
    public class AzureServiceBusBackplaneOptions
    {
        public string SubscriptionId {get;set;}
        public string TenantId {get;set;}
        public string ClientId {get;set;}
        public string ClientSecret {get;set;}

        public string ServiceBusConnectionString {get;set;}

        public string ResourceGroup {get;set;}
        public string Namespace {get;set;}
        public string Topic {get;set;}
    }

    public class AzureServiceBusBackplane : BackgroundService, IMessagingBackplane
    {
        private readonly AzureServiceBusBackplaneOptions _options;
        private readonly ILogger<AzureServiceBusBackplane> _logger;

        private readonly ConcurrentDictionary<string, AzureServiceBusSubscription> _subscriptions = new ConcurrentDictionary<string, AzureServiceBusSubscription>();
        private readonly ConcurrentQueue<AzureServiceBusSubscription> _pending = new ConcurrentQueue<AzureServiceBusSubscription>();
        private readonly SemaphoreSlim _subscribeSignal = new SemaphoreSlim(0);
        private readonly SemaphoreSlim _unsubscribeSignal = new SemaphoreSlim(0);

        private readonly TopicClient _topicClient;

        public AzureServiceBusBackplane(
            IOptions<AzureServiceBusBackplaneOptions> options,
            ILogger<AzureServiceBusBackplane> logger)
        {
            _options = options.Value;
            _logger = logger;

            _topicClient = new TopicClient(_options.ServiceBusConnectionString, _options.Topic, RetryPolicy.NoRetry);
        }

        public async Task Publish(string topic, string message, CancellationToken token)
        {
            var datagram = new Message();
            datagram.Body = Encoding.UTF8.GetBytes(message);
            datagram.Label = topic;
            await _topicClient.SendAsync(datagram).ConfigureAwait(false);
        }

        public ISubscription Subscribe(string topic)
        {
            var subscription = new AzureServiceBusSubscription(topic, _unsubscribeSignal);
            _pending.Enqueue(subscription);
            _subscribeSignal.Release();
            return subscription;
        }

        internal async Task<TokenCredentials> GetCredentialsAsync(CancellationToken token)
        {
            var context = new AuthenticationContext($"https://login.microsoftonline.com/{_options.TenantId}");
            var authResult = await context.AcquireTokenAsync(
                "https://management.azure.com/",
                new ClientCredential(_options.ClientId, _options.ClientSecret)
            ).ConfigureAwait(false);

            return new TokenCredentials(authResult.AccessToken);
        }

        internal async Task<SBSubscription> InitializeAzureServiceBus(CancellationToken token)
        {
            _logger.LogInformation("initializing azure service bus backend");

            var creds = await GetCredentialsAsync(token).ConfigureAwait(false);
            var client = new ServiceBusManagementClient(creds)
            {
                SubscriptionId = _options.SubscriptionId,
            };

            var subscriptionParams = new SBSubscription
            {
                AutoDeleteOnIdle = TimeSpan.FromMinutes(5),
                MaxDeliveryCount = 1,
            };

            _logger.LogInformation("registering subscription");
            var subscription = await client.Subscriptions.CreateOrUpdateAsync(
                resourceGroupName: _options.ResourceGroup,
                namespaceName: _options.Namespace,
                topicName: _options.Topic,
                subscriptionName: Environment.MachineName,
                parameters: subscriptionParams,
                cancellationToken: token
            ).ConfigureAwait(false);

            _logger.LogInformation("clearing existing rules");
            var allRules = await client.Rules.ListBySubscriptionsAsync(
                resourceGroupName: _options.ResourceGroup,
                namespaceName: _options.Namespace,
                topicName: _options.Topic,
                subscriptionName: subscription.Name,
                cancellationToken: token
            ).ConfigureAwait(false);

            foreach (var rule in allRules)
            {
                await client.Rules.DeleteAsync(
                    resourceGroupName: _options.ResourceGroup,
                    namespaceName: _options.Namespace,
                    topicName: _options.Topic,
                    subscriptionName: subscription.Name,
                    ruleName: rule.Name,
                    cancellationToken: token
                ).ConfigureAwait(false);
            }

            _logger.LogInformation("azure service bus backplane initialization complete");

            return subscription;
        }

        internal async Task AddRule(SBSubscription subscription, string newTopic, CancellationToken token)
        {
            _logger.LogDebug("adding rule for {topic} to subscription {subscription}", newTopic, subscription.Name);

            var creds = await GetCredentialsAsync(token).ConfigureAwait(false);
            var client = new ServiceBusManagementClient(creds)
            {
                SubscriptionId = _options.SubscriptionId,
            };

            var filter = new Microsoft.Azure.Management.ServiceBus.Models.CorrelationFilter();
            filter.Label = newTopic;

            var rule = new Rule();
            rule.CorrelationFilter = filter;

            await client.Rules.CreateOrUpdateAsync(
                resourceGroupName: _options.ResourceGroup,
                namespaceName: _options.Namespace,
                topicName: _options.Topic,
                subscriptionName: subscription.Name,
                ruleName: newTopic,
                parameters: rule,
                cancellationToken: token
            ).ConfigureAwait(false);

            _logger.LogDebug("rule for {topic} added to subscription {subscription}", newTopic, subscription.Name);
        }

        internal async Task DeleteRule(SBSubscription subscription, string deleteTopic, CancellationToken token)
        {
            _logger.LogDebug("deleting rule for {topic} from subscription {subscription}", deleteTopic, subscription.Name);

            var creds = await GetCredentialsAsync(token).ConfigureAwait(false);
            var client = new ServiceBusManagementClient(creds)
            {
                SubscriptionId = _options.SubscriptionId,
            };

            await client.Rules.DeleteAsync(
                resourceGroupName: _options.ResourceGroup,
                namespaceName: _options.Namespace,
                topicName: _options.Topic,
                subscriptionName: subscription.Name,
                ruleName: deleteTopic,
                cancellationToken: token
            ).ConfigureAwait(false);

            _logger.LogDebug("rule for {topic} from subscription {subscription}", deleteTopic, subscription.Name);
        }

        internal async Task UpdateRules(SBSubscription subscription, CancellationToken token)
        {
            while (_pending.TryDequeue(out var topicSub))
            {
                try
                {
                    await AddRule(subscription, topicSub.Topic, token).ConfigureAwait(false);
                    _subscriptions.TryAdd(topicSub.Topic, topicSub);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "error while trying to add rule for {topic}", topicSub.Topic);
                    _pending.Enqueue(topicSub);
                    throw;
                }
            }

            foreach (var s in _subscriptions)
            {
                if (s.Value.Disposed)
                {
                    _subscriptions.TryRemove(s.Key, out var _);
                    await DeleteRule(subscription, s.Key, token);
                }
            }
        }

        internal void HandleBackplaneMessage(SubscriptionClient client, Message message, CancellationToken token)
        {
            foreach (var subscription in _subscriptions)
            {
                if (subscription.Key == message.Label)
                {
                    var decoded = Encoding.UTF8.GetString(message.Body);
                    subscription.Value.Push(decoded);
                }
            }
        }

        protected override async Task ExecuteAsync(CancellationToken token)
        {
            while (true)
            {
                SubscriptionClient serviceBusClient = null;

                try
                {
                    var subscription = await InitializeAzureServiceBus(token).ConfigureAwait(false);

                    serviceBusClient = new SubscriptionClient(
                        connectionString: _options.ServiceBusConnectionString,
                        topicPath: _options.Topic,
                        subscriptionName: subscription.Name,
                        receiveMode: ReceiveMode.PeekLock);

                    var opts = new MessageHandlerOptions(ex =>
                    {
                        _logger.LogError(ex.Exception, "unhandled exception in message handler");
                        return Task.FromResult(0);
                    })
                    {
                        MaxConcurrentCalls = 1,
                    };

                    serviceBusClient.RegisterMessageHandler((msg, token) =>
                    {
                        this.HandleBackplaneMessage(serviceBusClient, msg, token);
                        return Task.FromResult(0);
                    }, opts);

                    Task newSubscriptionSignal = _subscribeSignal.WaitAsync();
                    Task subscriptionDisposeSignal = _unsubscribeSignal.WaitAsync();

                    while (true)
                    {
                        try
                        {
                            var result = await Task.WhenAny(newSubscriptionSignal, subscriptionDisposeSignal);
                            if (result == newSubscriptionSignal)
                            {
                                newSubscriptionSignal = _subscribeSignal.WaitAsync();
                            }
                            else if (result == subscriptionDisposeSignal)
                            {
                                subscriptionDisposeSignal = _unsubscribeSignal.WaitAsync();
                            }

                            await UpdateRules(subscription, token);
                        }
                        catch (TaskCanceledException)
                        {
                            _logger.LogDebug("inner loop cancelled");
                            throw;
                        }
                        catch(Exception ex)
                        {
                            _logger.LogError(ex, "unhandled exeption in inner control loop");
                        }
                    }
                }
                catch (TaskCanceledException)
                {
                    _logger.LogDebug("outer loop cancelled");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "unhandled exception in outer loop control loop");
                }

                _logger.LogDebug("closing backplane connection");
                Task close = serviceBusClient?.CloseAsync();
                if (close != null)
                {
                    await close;
                }

                _logger.LogDebug("something happend in the loop, retrying after a short delay");
                await Task.Delay(100);
            }
        }
    }

    public class AzureServiceBusSubscription : ISubscription
    {
        public bool Disposed {get; private set; } = false;

        public string Topic {get;}

        private readonly SemaphoreSlim _disposeSignal;

        private readonly SemaphoreSlim _messageSignal = new SemaphoreSlim(0);
        private readonly ConcurrentQueue<string> _messageQueue = new ConcurrentQueue<string>();

        public AzureServiceBusSubscription(string topic, SemaphoreSlim disposeSignal)
        {
            Topic = topic;
            _disposeSignal = disposeSignal;
        }

        public void Dispose()
        {
            Disposed = true;
            _disposeSignal.Release();
        }

        internal void Push(string message)
        {
            _messageQueue.Enqueue(message);
            _messageSignal.Release();
        }

        public async Task<string> Next(CancellationToken token)
        {
            await _messageSignal.WaitAsync(token);
            _messageQueue.TryDequeue(out var result);
            return result;
        }
    }
}