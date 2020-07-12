using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using System.Collections.Generic;
using System.Net.WebSockets;
using System;
using System.Reactive;
using System.Text;
using System.Linq;
using System.Collections.Concurrent;

namespace traction_sample
{
    internal class RoomData 
    {
        public string Room { get; set; }
        public List<ClientData> Clients {get;set;}
        public ISubscription Subscription {get;set;}
        public Task<string> Receive {get;set;}
        public CancellationTokenSource CancelReceive {get;set;}
    }

    internal class ClientData
    {
        public string Room {get; set;}
        public TaskCompletionSource<bool> Handle {get;set;}
        public WebSocket Socket {get;set;}
        public List<byte[]> Buffers {get;set;} = new List<byte[]>();
        public Task<WebSocketReceiveResult> Receive {get;set;}
        public CancellationTokenSource CancelReceive {get;set;}
    }

    internal class SendData
    {
        public Task Send {get;set;}
        public CancellationTokenSource CancelSend {get;set;}
    }

    internal class ConnectionManagementService : BackgroundService
    {
        private readonly SortedList<string, RoomData> _rooms = new SortedList<string, RoomData>();
        private readonly List<SendData> _sendOperations = new List<SendData>();
        private readonly ConcurrentQueue<ClientData> _pendingConnections = new ConcurrentQueue<ClientData>();
        private TaskCompletionSource<bool> _newClient = new TaskCompletionSource<bool>();

        private readonly IMessagingBackplane _backplane;

        public ConnectionManagementService(IMessagingBackplane backplane)
        {
            _backplane = backplane;
        }

        public const int BUFFER_SIZE = 1 << 12;

        public Task RegisterClient(string roomId, WebSocket socket)
        {
            var requestEnd = new TaskCompletionSource<bool>();
            var data = new ClientData
            {
                Room = roomId,
                Socket = socket,
                Handle = requestEnd,
            };

            BeginReceive(socket, data);

            _pendingConnections.Enqueue(data);

            _newClient.TrySetResult(true);

            return requestEnd.Task;
        }

        private static void BeginReceive(WebSocket socket, ClientData data)
        {
            var rxBuffer = new byte[BUFFER_SIZE];
            var rxCancel = new CancellationTokenSource();

            data.Receive = socket.ReceiveAsync(new ArraySegment<byte>(rxBuffer), rxCancel.Token);
            data.Buffers.Add(rxBuffer);
            data.CancelReceive = rxCancel;
        }

        private static void BeginReceive(RoomData data)
        {
            var rxCancel = new CancellationTokenSource();
            data.Receive = data.Subscription.Next(rxCancel.Token);
            data.CancelReceive = rxCancel;
        }

        private static void BeginSend(string message, WebSocket socket, SendData data)
        {
            var encoded = Encoding.UTF8.GetBytes(message);
            var sendCancel = new CancellationTokenSource();
            data.Send = socket.SendAsync(new ArraySegment<byte>(encoded), WebSocketMessageType.Text, true, sendCancel.Token);
            data.CancelSend = sendCancel;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (true)
            {
                while (_pendingConnections.TryDequeue(out var client))
                {
                    RoomData room;
                    if (!_rooms.TryGetValue(client.Room, out room))
                    {
                        room = new RoomData
                        {
                            Room = client.Room,
                            Subscription = _backplane.Subscribe(client.Room),
                            Clients = new List<ClientData> { client },
                        };
                        _rooms.TryAdd(client.Room, room);
                    }
                    else
                    {
                        room.Clients.Add(client);
                    }
                    BeginReceive(room);
                }


                var roomList = new List<RoomData>();
                var roomRxTaskList = new List<Task<string>>();
                var clientList = new List<ClientData>();
                var clientRxTaskList = new List<Task<WebSocketReceiveResult>>();
                var sendTaskList = new List<Task>();

                foreach (var (idx, pair) in _rooms.Select((pair, idx) => (idx, pair)))
                {
                    roomRxTaskList.Add(pair.Value.Receive);

                    foreach (var client in pair.Value.Clients)
                    {
                        clientRxTaskList.Add(client.Receive);
                    }
                }

                foreach (var send in _sendOperations)
                {
                    sendTaskList.Add(send.Send);
                }

                var whenRoom = Task.WhenAny(roomRxTaskList);
                var whenClient = Task.WhenAny(clientRxTaskList);
                var whenSend = Task.WhenAny(sendTaskList);

                var completed = await Task.WhenAny(whenRoom, whenClient, whenSend).ConfigureAwait(false);

                if (completed == whenRoom)
                {
                    var roomIdx = roomRxTaskList.IndexOf(whenRoom.Result);
                    var room = roomList[roomIdx];

                    if (whenRoom.Result.IsCompletedSuccessfully)
                    {
                        foreach (var client in room.Clients)
                        {
                            var data = new SendData();
                            BeginSend(whenRoom.Result.Result, client.Socket, data);
                            _sendOperations.Add(data);
                        }
                    }

                    
                }

                var taskIdx = await Task.WhenAny(taskList)

                if (taskIdx < totalRooms)
                {
                    var newMessageRoom = roomList[taskIdx];
                    if (newMessageRoom.Receive.IsCompletedSuccessfully)
                    {
                        var msg = newMessageRoom.Receive.Result;
                    }
                }
            }
        }
    }

}