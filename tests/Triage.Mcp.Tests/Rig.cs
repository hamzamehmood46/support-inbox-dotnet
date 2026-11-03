using System.IO.Pipelines;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Triage.Copilot;
using Triage.Engine;
using Triage.Mcp;

namespace Triage.Mcp.Tests;

/// <summary>A real MCP server and a real MCP client joined by in-memory pipes, so tests see exactly what a client such as Claude would.</summary>
public sealed class Rig : IAsyncDisposable
{
    private readonly McpServer _server;
    private readonly CancellationTokenSource _stop;
    private readonly Task _running;

    private Rig(McpServer server, Task running, CancellationTokenSource stop, McpClient client, Desk desk, TriageCopilot copilot, FakeChatClient chat)
    {
        _server = server;
        _running = running;
        _stop = stop;
        Client = client;
        Desk = desk;
        Copilot = copilot;
        Chat = chat;
    }

    public McpClient Client { get; }
    public Desk Desk { get; }
    public TriageCopilot Copilot { get; }
    public FakeChatClient Chat { get; }

    public static async Task<Rig> StartAsync()
    {
        var (desk, clock) = TriageMcp.DemoDesk();
        var chat = new FakeChatClient();
        var assistant = new TriageAssistant(chat);
        var copilot = new TriageCopilot(desk, assistant, clock);
        var options = TriageMcp.Options(new TriageTools(desk, copilot, assistant));

        var toServer = new Pipe();
        var toClient = new Pipe();
        var server = McpServer.Create(new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream(), "triage-mcp"), options);
        var stop = new CancellationTokenSource();
        var running = server.RunAsync(stop.Token);
        var client = await McpClient.CreateAsync(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()));
        return new Rig(server, running, stop, client, desk, copilot, chat);
    }

    public async Task<CallToolResult> Call(string tool, params (string Name, object Value)[] args) =>
        await Client.CallToolAsync(tool, args.ToDictionary(a => a.Name, a => (object?)a.Value)!);

    public Ticket Ticket(string key) => Desk.Ticket(key)!;

    public static string Text(CallToolResult r) => r.Content.OfType<TextContentBlock>().Single().Text;

    public static JsonElement Body(CallToolResult r) => JsonDocument.Parse(Text(r)).RootElement.Clone();

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await _stop.CancelAsync();
        try
        {
            await _running;
        }
        catch (OperationCanceledException)
        {
        }

        await _server.DisposeAsync();
    }
}
