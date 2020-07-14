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
using System.Text;
using System.Net.WebSockets;

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
                Path = $"/api/rooms/{id}",
            }.Uri;
        }

        [Fact]
        public async Task TestConnectionToNewRoom()
        {
            var backplane = new Mock<IMessagingBackplane>();
            var subscription = new Mock<ISubscription>();

            var nextComplete = new TaskCompletionSource<bool>();
            subscription.Setup(s => s.Next(It.IsAny<CancellationToken>()))
                .Callback(() => nextComplete.TrySetResult(true))
                .Returns(Task.Delay(-1).ContinueWith(t => ""));

            var subscribeComplete = new TaskCompletionSource<bool>();
            backplane.Setup(b => b.Subscribe(It.Is<string>(s => s == "foobar")))
                .Callback(() => subscribeComplete.TrySetResult(true))
                .Returns(subscription.Object);

            var factory = _factory.WithWebHostBuilder(cfg =>
            {
                cfg.ConfigureTestServices(svc =>
                {
                    svc.AddSingleton<IMessagingBackplane>(backplane.Object);
                });

                cfg.UseEnvironment("Test");
            });
            var client = factory.Server.CreateWebSocketClient();
            var socket = await client.ConnectAsync(GetRoomUri("foobar"), CancellationToken.None);

            var nextResult = await Task.WhenAny(nextComplete.Task, Task.Delay(1000));
            var subscribeResult = await Task.WhenAny(subscribeComplete.Task, Task.Delay(1000));

            Assert.Equal(nextComplete.Task, nextResult);
            Assert.Equal(subscribeComplete.Task, subscribeResult);
        }

        [Fact]
        public async Task TestSecondRoomConnection()
        {
            var backplane = new Mock<IMessagingBackplane>();
            var subscription = new Mock<ISubscription>();

            var nextCalled = new SemaphoreSlim(0);
            subscription.Setup(s => s.Next(It.IsAny<CancellationToken>()))
                .Callback(() => nextCalled.Release())
                .Returns(Task.Delay(-1).ContinueWith(t => ""));

            var subscribeCalled = new SemaphoreSlim(0);
            backplane.Setup(b => b.Subscribe(It.Is<string>(s => s == "foobar")))
                .Callback(() => subscribeCalled.Release())
                .Returns(subscription.Object);

            var factory = _factory.WithWebHostBuilder(cfg =>
            {
                cfg.ConfigureTestServices(svc =>
                {
                    svc.AddSingleton<IMessagingBackplane>(backplane.Object);
                });

                cfg.UseEnvironment("Test");
            });
            var client = factory.Server.CreateWebSocketClient();
            var socket1 = await client.ConnectAsync(GetRoomUri("foobar"), CancellationToken.None);
            var socket2 = await client.ConnectAsync(GetRoomUri("foobar"), CancellationToken.None);

            Assert.True(await nextCalled.WaitAsync(1000));
            Assert.True(await subscribeCalled.WaitAsync(1000));

            var wait = subscribeCalled.WaitAsync();
            var subscribeResult = await Task.WhenAny(wait, Task.Delay(100));

            Assert.NotEqual(wait, subscribeResult);
        }

        [Fact]
        public async Task TestMultipleConnectionsDifferentRooms()
        {
            var backplane = new Mock<IMessagingBackplane>();
            var subscription = new Mock<ISubscription>();

            var nextCalled = new SemaphoreSlim(0);
            subscription.Setup(s => s.Next(It.IsAny<CancellationToken>()))
                .Callback(() => nextCalled.Release())
                .Returns(Task.Delay(-1).ContinueWith(t => ""));

            var subscribeCalled = new SemaphoreSlim(0);
            backplane.Setup(b => b.Subscribe(It.IsAny<string>()))
                .Callback(() => subscribeCalled.Release())
                .Returns(subscription.Object);

            var factory = _factory.WithWebHostBuilder(cfg =>
            {
                cfg.ConfigureTestServices(svc =>
                {
                    svc.AddSingleton<IMessagingBackplane>(backplane.Object);
                });

                cfg.UseEnvironment("Test");
            });
            var client = factory.Server.CreateWebSocketClient();
            var socket1 = await client.ConnectAsync(GetRoomUri("foobar"), CancellationToken.None);
            var socket2 = await client.ConnectAsync(GetRoomUri("foobaz"), CancellationToken.None);

            Assert.True(await nextCalled.WaitAsync(1000));
            Assert.True(await subscribeCalled.WaitAsync(1000));

            await nextCalled.WaitAsync(1000);
            await subscribeCalled.WaitAsync(1000);
        }

        [Fact]
        public async Task TestReceiveSingleBackplaneMessage()
        {
            var backplane = new Mock<IMessagingBackplane>();
            var subscription = new Mock<ISubscription>();

            bool msgSent = false;

            var nextCalled = new SemaphoreSlim(0);
            subscription.Setup(s => s.Next(It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    nextCalled.Release();
                    if (!msgSent)
                    {
                        msgSent = true;
                        return Task.FromResult("message1");
                    }
                    else
                    {
                        return Task.Delay(-1).ContinueWith(t => "");
                    }
                });

            backplane.Setup(b => b.Subscribe(It.IsAny<string>()))
                .Returns(subscription.Object);

            var factory = _factory.WithWebHostBuilder(cfg =>
            {
                cfg.ConfigureTestServices(svc =>
                {
                    svc.AddSingleton<IMessagingBackplane>(backplane.Object);
                });

                cfg.UseEnvironment("Test");
            });
            var client = factory.Server.CreateWebSocketClient();
            var socket1 = await client.ConnectAsync(GetRoomUri("foobar"), CancellationToken.None);

            var buffer = new byte[1024];
            var result = await socket1.ReceiveAsync(new ArraySegment<byte>(buffer),CancellationToken.None);
            var message = Encoding.UTF8.GetString(buffer, 0, result.Count);

            Assert.Equal("message1", message);

            Assert.True(await nextCalled.WaitAsync(100));
            Assert.True(await nextCalled.WaitAsync(100));
            Assert.False(await nextCalled.WaitAsync(100));
        }

        [Fact]
        public async Task TestReceiveMultipleBackplaneMessage()
        {
            var backplane = new Mock<IMessagingBackplane>();
            var subscription = new Mock<ISubscription>();

            int msgSent = 0;

            var nextCalled = new SemaphoreSlim(0);
            subscription.Setup(s => s.Next(It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    nextCalled.Release();
                    if (msgSent < 3)
                    {
                        msgSent++;
                        return Task.FromResult($"message{msgSent}");
                    }
                    else
                    {
                        return Task.Delay(-1).ContinueWith(t => "");
                    }
                });

            backplane.Setup(b => b.Subscribe(It.IsAny<string>()))
                .Returns(subscription.Object);

            var factory = _factory.WithWebHostBuilder(cfg =>
            {
                cfg.ConfigureTestServices(svc =>
                {
                    svc.AddSingleton<IMessagingBackplane>(backplane.Object);
                });

                cfg.UseEnvironment("Test");
            });
            var client = factory.Server.CreateWebSocketClient();
            var socket1 = await client.ConnectAsync(GetRoomUri("foobar"), CancellationToken.None);

            var buffer = new byte[1024];
            var result = await socket1.ReceiveAsync(new ArraySegment<byte>(buffer),CancellationToken.None);
            var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
            Assert.Equal("message1", message);

            result = await socket1.ReceiveAsync(new ArraySegment<byte>(buffer),CancellationToken.None);
            message = Encoding.UTF8.GetString(buffer, 0, result.Count);
            Assert.Equal("message2", message);

            result = await socket1.ReceiveAsync(new ArraySegment<byte>(buffer),CancellationToken.None);
            message = Encoding.UTF8.GetString(buffer, 0, result.Count);
            Assert.Equal("message3", message);

            Assert.True(await nextCalled.WaitAsync(100));
            Assert.True(await nextCalled.WaitAsync(100));
            Assert.True(await nextCalled.WaitAsync(100));
            Assert.True(await nextCalled.WaitAsync(100));
            Assert.False(await nextCalled.WaitAsync(100));
        }

        [Fact]
        public async Task TestSingleRoomClientSendMessage()
        {
            var backplane = new Mock<IMessagingBackplane>();
            var subscription = new Mock<ISubscription>();

            subscription.Setup(s => s.Next(It.IsAny<CancellationToken>()))
                .Returns(() => Task.Delay(-1).ContinueWith(t => ""));

            backplane.Setup(b => b.Subscribe(It.IsAny<string>()))
                .Returns(subscription.Object);

            string topicResult = null;
            string messageResult = null;

            var signal = new SemaphoreSlim(0);
            backplane.Setup(b => b.Publish(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback((string topic, string message, CancellationToken token) => 
                {
                    topicResult = topic;
                    messageResult = message;
                    signal.Release();                
                })
                .Returns(Task.FromResult(0));

            var factory = _factory.WithWebHostBuilder(cfg =>
            {
                cfg.ConfigureTestServices(svc =>
                {
                    svc.AddSingleton<IMessagingBackplane>(backplane.Object);
                });

                cfg.UseEnvironment("Test");
            });
            var client = factory.Server.CreateWebSocketClient();
            var socket1 = await client.ConnectAsync(GetRoomUri("foobar"), CancellationToken.None);

            var buffer = Encoding.UTF8.GetBytes("message0");
            await socket1.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);

            Assert.True(await signal.WaitAsync(100));
            Assert.Equal("foobar", topicResult);
            Assert.Equal("message0", messageResult);

            var recv = socket1.ReceiveAsync(new ArraySegment<byte>(new byte[1024]), CancellationToken.None);
            var result = await Task.WhenAny(recv, Task.Delay(100));
            Assert.NotEqual(recv, result);
        }

        internal static int FillBuffer(string message, byte[] data)
        {
            return Encoding.UTF8.GetBytes(message, 0, message.Length, data, 0);
        }

        [Fact]
        public async Task TestMultiClientRoomSendMessage()
        {
            var backplane = new Mock<IMessagingBackplane>();
            var subscription = new Mock<ISubscription>();

            subscription.Setup(s => s.Next(It.IsAny<CancellationToken>()))
                .Returns(() => Task.Delay(-1).ContinueWith(t => ""));

            backplane.Setup(b => b.Subscribe(It.IsAny<string>()))
                .Returns(subscription.Object);

            string topicResult = null;
            string messageResult = null;

            backplane.Setup(b => b.Publish(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.FromResult(0));

            var factory = _factory.WithWebHostBuilder(cfg =>
            {
                cfg.ConfigureTestServices(svc =>
                {
                    svc.AddSingleton<IMessagingBackplane>(backplane.Object);
                });

                cfg.UseEnvironment("Test");
            });
            var client = factory.Server.CreateWebSocketClient();
            var socket1 = await client.ConnectAsync(GetRoomUri("foobar"), CancellationToken.None);
            var socket2 = await client.ConnectAsync(GetRoomUri("foobar"), CancellationToken.None);

            var buffer1 = new byte[1024];
            var buffer2 = new byte[1024];
            var bufferLength = FillBuffer("message0", buffer1);
            await socket1.SendAsync(new ArraySegment<byte>(buffer1, 0, bufferLength), WebSocketMessageType.Text, true, CancellationToken.None);

            var recv2 = await socket2.ReceiveAsync(new ArraySegment<byte>(buffer2), CancellationToken.None);
            var message = Encoding.UTF8.GetString(buffer2, 0, recv2.Count);
            Assert.Equal("message0", message);

            bufferLength = FillBuffer("message1", buffer2);
            await socket2.SendAsync(new ArraySegment<byte>(buffer2, 0, bufferLength), WebSocketMessageType.Text, true, CancellationToken.None);

            var recv1 = await socket1.ReceiveAsync(new ArraySegment<byte>(buffer1), CancellationToken.None);
            message = Encoding.UTF8.GetString(buffer1, 0, recv1.Count);
            Assert.Equal("message1", message);

            var recvFail = socket1.ReceiveAsync(new ArraySegment<byte>(buffer1), CancellationToken.None);
            var result = await Task.WhenAny(recvFail, Task.Delay(100));
            Assert.NotEqual(recvFail, result);

            recvFail = socket2.ReceiveAsync(new ArraySegment<byte>(buffer2), CancellationToken.None);
            result = await Task.WhenAny(recvFail, Task.Delay(100));
            Assert.NotEqual(recvFail, result);
        }
    }
}
