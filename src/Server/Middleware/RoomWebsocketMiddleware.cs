using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Server.Middleware
{
    public class RoomWebsocketMiddleware
    {
        private readonly RequestDelegate _next;

        public RoomWebsocketMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var roomId = context.GetRouteValue("id") as string;
            var svc = context.RequestServices.GetRequiredService<ConnectionManagementService>();
            if (context.WebSockets.IsWebSocketRequest && roomId != null)
            {
                var socket = await context.WebSockets.AcceptWebSocketAsync();
                await svc.RegisterConnection(roomId, socket);
            }
            else
            {
                await _next(context);
            }
        }
    }

    public static class RoomWebsocketMiddlewareExtensions
    {
        public static IEndpointConventionBuilder MapRoomWebsocketEndpoint(
            this IEndpointRouteBuilder endpoints, string pattern)
        {
            var pipeline = endpoints.CreateApplicationBuilder()
                .UseMiddleware<RoomWebsocketMiddleware>()
                .Build();

            return endpoints.Map(pattern, pipeline).WithDisplayName("Room Websocket");
        }
    }
}