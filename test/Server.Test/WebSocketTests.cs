using System;
using Xunit;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Tasks;
using System.Threading;
using Moq;
using Server.Messaging;

namespace Server.Test
{
    public class WebSocketTests : IClassFixture<WebApplicationFactory<Startup>>
    {
        private readonly WebApplicationFactory<Startup> _factory;

        public WebSocketTests(WebApplicationFactory<Startup> factory)
        {
            _factory = factory;
        }

        private Uri GetRoomUri(string id)
        {
            return new UriBuilder(_factory.Server.BaseAddress) 
            {
                Scheme = "ws",
                Path = $"/rooms/{id}",
            }.Uri;
        }

        [Fact]
        public async Task TestConnectionToNewRoom()
        {
            var backplane = new Mock<IMessagingBackplane>();
            var subscription = new Mock<ISubscription>();

            var nextCallCount = 0;
            subscription.Setup(s => s.Next(It.IsAny<CancellationToken>()))
                .Callback(() => nextCallCount++)
                .Returns(Task.Delay(-1).ContinueWith(t => ""));

            var setupCallCount = 0;
            backplane.Setup(b => b.Subscribe(It.Is<string>(s => s == "foobar")))
                .Callback(() => setupCallCount++)
                .Returns(subscription.Object);

            var factory = _factory.WithWebHostBuilder(cfg =>
            {
                cfg.ConfigureTestServices(svc =>
                {
                    svc.AddSingleton<IMessagingBackplane>(backplane.Object);
                });
            });
            var client = factory.Server.CreateWebSocketClient();
            var socket = await client.ConnectAsync(GetRoomUri("foobar"), CancellationToken.None);

            Assert.Equal(1, setupCallCount);
            Assert.Equal(1, nextCallCount);
        }
    }
}
