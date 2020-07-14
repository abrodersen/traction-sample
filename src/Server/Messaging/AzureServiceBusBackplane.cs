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

        private readonly List<AzureServiceBusSubscription> _subscriptions = new List<AzureServiceBusSubscription>();
        private readonly SemaphoreSlim _subscriptionLock = new SemaphoreSlim(1, 1);
        private readonly ConcurrentQueue<AzureServiceBusSubscription> _pending = new ConcurrentQueue<AzureServiceBusSubscription>();
        private readonly SemaphoreSlim _subscribeSignal = new SemaphoreSlim(0);
        private readonly SemaphoreSlim _unsubscribeSignal = new SemaphoreSlim(0);

        public AzureServiceBusBackplane(
            IOptions<AzureServiceBusBackplaneOptions> options,
            ILogger<AzureServiceBusBackplane> logger)
        {
            _options = options.Value;
            _logger = logger;
        }

        public async Task Initialize(CancellationToken token)
        {
            
        }

        public Task Publish(string topic, string message, CancellationToken token)
        {
            throw new System.NotImplementedException();
        }

        public ISubscription Subscribe(string topic)
        {
            throw new System.NotImplementedException();
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
        }

        protected override async Task ExecuteAsync(CancellationToken token)
        {
            while (true)
            {
                try
                {
                    var subscription = await InitializeAzureServiceBus(token).ConfigureAwait(false);

                    var serviceBusClient = new SubscriptionClient(
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

                    serviceBusClient.RegisterMessageHandler(async (msg, token) =>
                    {
                        if (!await _subscriptionLock.WaitAsync(100))
                        {
                            await serviceBusClient.AbandonAsync(msg.SystemProperties.LockToken);
                            return;
                        }

                        try
                        {
                            foreach (var subscription in _subscriptions)
                            {
                                if (subscription.Topic == msg.Label)
                                {
                                    var decoded = Encoding.UTF8.GetString(msg.Body);
                                    subscription.Push(decoded);
                                }
                            }
                        }
                        finally
                        {
                            _subscriptionLock.Release();
                        }
                    }, opts);

                    while (true)
                    {
                        try
                        {


                            while (_pending.TryDequeue(out var topicSub))
                            {
                                try
                                {
                                    await AddRule(subscription, topicSub.Topic, token).ConfigureAwait(false);
                                    _subscriptions.Add(topicSub);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "error while trying to add rule for {topic}", topicSub.Topic);
                                    _pending.Enqueue(topicSub);
                                    throw;
                                }
                            }

                            for (int i = _subscriptions.Count - 1; i >= 0; i--)
                            {
                                var oldSub = _subscriptions[i];
                                if (oldSub.Disposed)
                                {
                                    _subscriptions.RemoveAt(i);
                                }
                            }

                        }
                        catch(Exception ex)
                        {
                            _logger.LogError(ex, "unhandled exeption in inner control loop");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "unhandled exception in outer loop control loop");
                }

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

        public void Push(string message)
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