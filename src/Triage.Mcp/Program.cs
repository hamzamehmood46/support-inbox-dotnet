using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;
using Triage.Copilot;
using Triage.Mcp;

// Standard output belongs to the MCP protocol, so anything meant for a person goes to standard error.
var settings = CopilotSettings.FromEnvironment();
IChatClient chat;
try
{
    chat = CopilotClients.Create(settings);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"triage-mcp: {ex.Message}");
    return 1;
}

var (desk, clock) = TriageMcp.DemoDesk();
var assistant = new TriageAssistant(chat);
var copilot = new TriageCopilot(desk, assistant, clock);

Console.Error.WriteLine($"triage-mcp ready (demo desk, fake data). {settings}");
await using var server = McpServer.Create(new StdioServerTransport("triage-mcp"), TriageMcp.Options(new TriageTools(desk, copilot, assistant)));
await server.RunAsync();
return 0;
