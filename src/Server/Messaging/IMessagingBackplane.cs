using System;
using System.Threading;
using System.Threading.Tasks;

namespace Server.Messaging
{
    public interface IMessagingBackplane
    {
        ISubscription Subscribe(string topic);
        Task Publish(string topic, string message, CancellationToken token);
    }

    public interface ISubscription : IDisposable
    {
        Task<string> Next(CancellationToken token);
    }

}