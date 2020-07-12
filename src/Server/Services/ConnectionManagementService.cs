using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using System.Collections.Generic;
using System.Net.WebSockets;
using System;
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

        public string Message {get;set;}
        public ClientData Client {get; set;}
        public string Room {get;set;}
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

            BeginReceive(data);

            _pendingConnections.Enqueue(data);

            _newClient.TrySetResult(true);

            return requestEnd.Task;
        }

        private static void BeginReceive(ClientData data)
        {
            var rxBuffer = new byte[BUFFER_SIZE];
            var rxCancel = new CancellationTokenSource();

            data.Receive = data.Socket.ReceiveAsync(new ArraySegment<byte>(rxBuffer), rxCancel.Token);
            data.Buffers.Add(rxBuffer);
            data.CancelReceive = rxCancel;
        }

        private static void BeginReceive(RoomData data)
        {
            var rxCancel = new CancellationTokenSource();
            data.Receive = data.Subscription.Next(rxCancel.Token);
            data.CancelReceive = rxCancel;
        }

        private static bool IsSocketDead(WebSocket socket)
        {
            return socket.State == WebSocketState.Aborted ||
                socket.State == WebSocketState.Closed ||
                socket.State == WebSocketState.CloseReceived ||
                socket.State == WebSocketState.CloseSent;
        }

        private void BeginSend(SendData data)
        {
            var sendCancel = new CancellationTokenSource();
            if (data.Client != null)
            {
                var encoded = Encoding.UTF8.GetBytes(data.Message);
                data.Send = data.Client.Socket.SendAsync(new ArraySegment<byte>(encoded), WebSocketMessageType.Text, true, sendCancel.Token);
            }
            else if (data.Room != null)
            {
                data.Send = _backplane.Publish(data.Room, data.Message, sendCancel.Token);
            }

            data.CancelSend = sendCancel;
        }

        private IEnumerable<SendData> SendToRoom(string message, RoomData data)
        {
            foreach (var client in data.Clients)
            {
                var sendData = new SendData();
                sendData.Message = message;
                sendData.Client = client;
                BeginSend(sendData);
                yield return sendData;
            }
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

                var newSendOperations = new List<SendData>();

                foreach (var (idx, pair) in _rooms.Select((pair, idx) => (idx, pair)))
                {
                    roomList.Add(pair.Value);
                    roomRxTaskList.Add(pair.Value.Receive);

                    pair.Value.Clients.ForEach(client =>
                    {
                        if (IsSocketDead(client.Socket))
                        {
                            client.Socket.Abort();
                            pair.Value.Clients.Remove(client);
                        }
                        else
                        {
                            clientList.Add(client);
                            clientRxTaskList.Add(client.Receive);
                        }
                    });
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
                        _sendOperations.AddRange(SendToRoom(whenRoom.Result.Result, room));
                    }
                    else
                    {
                        room.Subscription?.Dispose();
                        room.Subscription = null;
                        room.Subscription = _backplane.Subscribe(room.Room);
                        BeginReceive(room);
                    }
                }
                else if (completed == whenClient)
                {
                    var clientIdx = clientRxTaskList.IndexOf(whenClient.Result);
                    var client = clientList[clientIdx];
                    var room = _rooms[client.Room];

                    if (whenClient.Result.IsCompletedSuccessfully)
                    {
                        var clientRxResult = whenClient.Result.Result;
                        if (clientRxResult.EndOfMessage)
                        {
                            var allBytes = new List<byte>();
                            foreach (var buffer in client.Buffers)
                            {
                                allBytes.AddRange(buffer);
                            }
                            var message = Encoding.UTF8.GetString(allBytes.ToArray());

                            newSendOperations.AddRange(SendToRoom(message, room));

                            var upstream = new SendData();
                            upstream.Message = message;
                            upstream.Room = room.Room;
                            BeginSend(upstream);
                            newSendOperations.Add(upstream);
                        }
                        else
                        {
                            BeginReceive(client);
                        }
                    }
                    else
                    {
                        client.Socket.Abort();
                        client.Receive = null;
                        client.CancelReceive = null;
                    }
                }
                else if (completed == whenSend)
                {
                    var sendIdx = sendTaskList.IndexOf(whenSend.Result);
                    var send = _sendOperations[sendIdx];

                    if (whenSend.Result.IsCompletedSuccessfully)
                    {
                        _sendOperations.RemoveAt(sendIdx);
                        send.Send = null;
                        send.Client = null;
                        send.CancelSend = null;
                    }
                    else if (send.Client != null && IsSocketDead(send.Client.Socket))
                    {
                        send.Client.Socket.Abort();
                        _sendOperations.RemoveAt(sendIdx);
                        send.Send = null;
                        send.Client = null;
                        send.CancelSend = null;
                    }
                    else
                    {
                        BeginSend(send);
                    }
                }

                _sendOperations.AddRange(newSendOperations);
            }
        }
    }

}