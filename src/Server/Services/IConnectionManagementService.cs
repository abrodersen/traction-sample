using System.Net.WebSockets;
using System.Threading.Tasks;

namespace Server
{
    public interface IConnectionManagementService
    {
        Task RegisterConnection(string roomId, WebSocket socket);
    }
}