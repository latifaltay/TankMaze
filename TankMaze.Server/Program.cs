using TankMaze.Server.Game;
using TankMaze.Server.Network;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<GameRoomManager>();
builder.Services.AddSingleton<SocketHandler>();
builder.Services.AddHostedService<GameLoopService>();

var app = builder.Build();

app.UseWebSockets();

app.Map("/ws", async context =>
{
    if (context.WebSockets.IsWebSocketRequest)
    {
        var socket = await context.WebSockets.AcceptWebSocketAsync();
        var handler = app.Services.GetRequiredService<SocketHandler>();
        await handler.HandleConnection(socket);
    }
    else
    {
        context.Response.StatusCode = 400;
    }
});

app.Run();
