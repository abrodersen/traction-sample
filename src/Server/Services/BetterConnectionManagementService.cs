using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading.Tasks;

namespace Server
{
    

    public class BetterConnectionManagementService : IConnectionManagementService
    {
        private Dictionary<string, RoomData> _rooms = new Dictionary<string, RoomData>();
        

        public Task RegisterConnection(string room, WebSocket socket)
        {
            throw new System.NotImplementedException();
        }

        internal class RoomData
        {
            public string RoomName {get;set;}
            public List<ClientData> Clients {get;set;}
            public Task<(string, RoomData)> Task {get;set;}
        }

        internal class ClientData
        {
            public WebSocket Socket { get; set; }
            public Task<(WebSocketReceiveResult, ClientData)> Task {get; set;}
        }
    }
}