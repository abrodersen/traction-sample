using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using System.Collections.Generic;
using System.Net.WebSockets;
using System;
using System.Text;
using System.Linq;
using System.Collections.Concurrent;
using Server.Messaging;
using Microsoft.Extensions.Logging;

namespace Server
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
        public const int BUFFER_SIZE = 1 << 12;

        public long Id {get;set;}
        public string Room {get; set;}
        public TaskCompletionSource<bool> Handle {get;set;}
        public WebSocket Socket {get;set;}

        public byte[] Buffer {get;set;} = new byte[BUFFER_SIZE];
        public List<byte> CurrentMessage {get;set;} = new List<byte>();
        public Task<WebSocketReceiveResult> Receive {get;set;}
        public CancellationTokenSource CancelReceive {get;set;}
    }

    internal enum SendType
    {
        Client,
        Backplane,
    }

    internal class SendData
    {
        public Task Send {get;set;}
        public CancellationTokenSource CancelSend {get;set;}

        public string Message {get;set;}
        public ClientData Client {get; set;}
        public string Room {get;set;}
    }

    internal class ConnectionManagementService : BackgroundService, IConnectionManagementService
    {
        private readonly SortedList<string, RoomData> _rooms = new SortedList<string, RoomData>();
        private readonly List<SendData> _sendOperations = new List<SendData>();
        private readonly ConcurrentQueue<ClientData> _pendingConnections = new ConcurrentQueue<ClientData>();
        private SemaphoreSlim _newClient = new SemaphoreSlim(0);

        private readonly IMessagingBackplane _backplane;
        private readonly ILogger<ConnectionManagementService> _logger;

        private long _connectionId = 0;

        public ConnectionManagementService(IMessagingBackplane backplane, ILogger<ConnectionManagementService> logger)
        {
            _backplane = backplane;
            _logger = logger;

            _logger.LogDebug("connection management service initialized");
        }

        

        public Task RegisterConnection(string roomId, WebSocket socket)
        {
            _logger.LogInformation("new client registered in room {room}", roomId);
            var requestEnd = new TaskCompletionSource<bool>();
            var data = new ClientData
            {
                Id = Interlocked.Increment(ref _connectionId),
                Room = roomId,
                Socket = socket,
                Handle = requestEnd,
            };

            BeginReceive(data);

            _pendingConnections.Enqueue(data);

            _logger.LogDebug("sending signal to wake event loop");
            _newClient.Release();
            _logger.LogDebug("signal sent");

            return requestEnd.Task;
        }

        private static void BeginReceive(ClientData data)
        {
            var rxCancel = new CancellationTokenSource();
            data.Receive = data.Socket.ReceiveAsync(new ArraySegment<byte>(data.Buffer), rxCancel.Token);
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
                _logger.LogDebug("sending data to client {client}", data.Client.Id);
                var encoded = Encoding.UTF8.GetBytes(data.Message);
                data.Send = data.Client.Socket.SendAsync(new ArraySegment<byte>(encoded), WebSocketMessageType.Text, true, sendCancel.Token);
            }
            else
            {
                _logger.LogDebug("sending data upstream channel for room {room}", data.Room);
                data.Send = _backplane.Publish(data.Room, data.Message, sendCancel.Token);
            }

            data.CancelSend = sendCancel;
        }

        private IEnumerable<SendData> SendToRoom(string message, RoomData data, long? sender = null)
        {
            foreach (var client in data.Clients)
            {
                if (client.Id == sender)
                {
                    continue;
                }

                var sendData = new SendData();
                sendData.Message = message;
                sendData.Client = client;
                sendData.Room = data.Room;
                BeginSend(sendData);
                yield return sendData;
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                while (true)
                {
                    _logger.LogDebug("loop start");

                    while (_pendingConnections.TryDequeue(out var client))
                    {
                        _logger.LogDebug("accepting new connection in room {room}", client.Room);
                        RoomData room;
                        if (!_rooms.TryGetValue(client.Room, out room))
                        {
                            _logger.LogDebug("room {room} does not exist, creating it now", client.Room);
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
                            _logger.LogDebug("room {room} already exists with {clients} cients", client.Room, room.Clients.Count);
                        }

                        _logger.LogDebug("opening backplane channel for room {room}", client.Room);
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

                        for (int i = pair.Value.Clients.Count - 1; i >= 0; i--)
                        {
                            var client = pair.Value.Clients[i];
                            if (IsSocketDead(client.Socket))
                            {
                                _logger.LogDebug("detected a dead client {client} in room {room}", client.Id, client.Room);
                                client.Socket.Abort();
                                pair.Value.Clients.RemoveAt(i);
                            }
                            else
                            {
                                clientList.Add(client);
                                clientRxTaskList.Add(client.Receive);
                            }
                        }
                    }

                    _logger.LogDebug("total rooms = {rooms}, total clients = {clients}", roomList.Count, clientList.Count);

                    foreach (var send in _sendOperations)
                    {
                        if (send.Client != null && IsSocketDead(send.Client.Socket))
                        {
                            _logger.LogDebug("killing send operation due to dead client {client}", send.Client.Id);
                        }
                        else
                        {
                            sendTaskList.Add(send.Send);
                        }
                    }

                    _logger.LogDebug("total pending send operations = {sends}", _sendOperations.Count);

                    Task<Task<string>> whenRoom = null;
                    if (roomRxTaskList.Any())
                    {
                        whenRoom = Task.WhenAny(roomRxTaskList);
                    }

                    Task<Task<WebSocketReceiveResult>> whenClient = null;
                    if (clientRxTaskList.Any())
                    {
                        whenClient = Task.WhenAny(clientRxTaskList);
                    }

                    Task<Task> whenSend = null;
                    if (sendTaskList.Any())
                    {
                        whenSend = Task.WhenAny(sendTaskList);
                    }

                    var newClientTask = _newClient.WaitAsync();
                    var globalTaskList = new Task[] { whenRoom, whenClient, whenSend, newClientTask}.Where(t => t != null).ToList();
                    _logger.LogDebug("waiting for signal from {tasks} tasks", globalTaskList.Count);
                    var completed = await Task.WhenAny(globalTaskList).ConfigureAwait(false);                
                    _logger.LogDebug("awoke");

                    if (completed == whenRoom)
                    {
                        var roomIdx = roomRxTaskList.IndexOf(whenRoom.Result);
                        var room = roomList[roomIdx];
                        _logger.LogDebug("received signal on upstream channel for room {room}", room.Room);

                        if (whenRoom.Result.IsCompletedSuccessfully)
                        {
                            _sendOperations.AddRange(SendToRoom(whenRoom.Result.Result, room));
                        }
                        else
                        {
                            room.Subscription?.Dispose();
                            room.Subscription = null;
                            room.Subscription = _backplane.Subscribe(room.Room);
                        }

                        _logger.LogDebug("beginning new receive operation on backplane channel");
                        BeginReceive(room);
                    }
                    else if (completed == whenClient)
                    {
                        var clientIdx = clientRxTaskList.IndexOf(whenClient.Result);
                        var client = clientList[clientIdx];
                        var room = _rooms[client.Room];
                        _logger.LogDebug("received signal on client channel for room {room}", room.Room);

                        if (whenClient.Result.IsCompletedSuccessfully)
                        {
                            var clientRxResult = whenClient.Result.Result;
                            var receivedData = Enumerable.Range(0, clientRxResult.Count).Select(i => client.Buffer[i]);
                            client.CurrentMessage.AddRange(receivedData);

                            if (clientRxResult.EndOfMessage)
                            {
                                var message = Encoding.UTF8.GetString(client.CurrentMessage.ToArray());
                                _logger.LogDebug("received message {message}", message);
                                client.CurrentMessage.Clear();

                                newSendOperations.AddRange(SendToRoom(message, room, client.Id));

                                var upstream = new SendData();
                                upstream.Message = message;
                                upstream.Room = room.Room;
                                BeginSend(upstream);
                                newSendOperations.Add(upstream);
                            }

                            _logger.LogDebug("queuing up new receive operation on client channel");
                            BeginReceive(client);
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
                        if (send.Client != null)
                        {
                            _logger.LogDebug("received signal on client send operation for client {client}", send.Client.Id);
                        }
                        else
                        {
                            _logger.LogDebug("received signal on backplane send operation for room {room}", send.Room);
                        }

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
                    else if (completed == newClientTask)
                    {
                        _logger.LogDebug("receivied signal on new client channel");
                    }

                    _sendOperations.AddRange(newSendOperations);

                    _logger.LogDebug("loop end");
                }
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "unhandled exception in core event loop");
            }
        }
    }

}