using System.Threading;
using System.Threading.Tasks;

namespace Server.Messaging
{
    public class DummyBackplane : IMessagingBackplane
    {
        public Task Publish(string topic, string message, CancellationToken token)
        {
            return Task.Delay(100);
        }

        public ISubscription Subscribe(string topic)
        {
            return new DummySubscription();
        }
    }

    public class DummySubscription : ISubscription
    {
        private int _count = 0;

        public void Dispose()
        {
        }

        public async Task<string> Next(CancellationToken token)
        {
            await Task.Delay(2500);
            return (_count++).ToString();
        }
    }
}