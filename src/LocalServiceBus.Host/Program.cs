using System.CommandLine;
using System.Text;
using LocalServiceBus;
using LocalServiceBus.Amqp;
using LocalServiceBus.Core.Engine;
using LocalServiceBus.Management;
using Microsoft.AspNetCore.Builder;

var portOption = new Option<int>("--port") { DefaultValueFactory = _ => 5672, Description = "AMQP listener port" };
var apiOption = new Option<int>("--api") { DefaultValueFactory = _ => 9090, Description = "REST management API port" };
var configOption = new Option<string?>("--config") { Description = "Path to localbus.config.json" };

// --- start command ---
var startCommand = new Command("start", "Start the LocalServiceBus emulator");
startCommand.Options.Add(portOption);
startCommand.Options.Add(apiOption);
startCommand.Options.Add(configOption);
startCommand.SetAction(async (parseResult, ct) =>
{
    var port = parseResult.GetValue(portOption);
    var apiPort = parseResult.GetValue(apiOption);
    var configPath = parseResult.GetValue(configOption);

    var broker = new MessageBroker();

    if (configPath is not null && File.Exists(configPath))
    {
        var json = await File.ReadAllTextAsync(configPath, ct);
        ConfigLoader.LoadConfig(broker, json);
    }

    using var amqpHost = new AmqpListenerHost(broker, port);
    amqpHost.Start();

    var builder = WebApplication.CreateBuilder();
    builder.WebHost.ConfigureKestrel(k => k.ListenLocalhost(apiPort));
    builder.Logging.ClearProviders();
    var app = builder.Build();
    app.MapManagementEndpoints(broker);

    Console.WriteLine($"LocalServiceBus Emulator started");
    Console.WriteLine($"  AMQP:  amqp://localhost:{port}");
    Console.WriteLine($"  REST:  http://localhost:{apiPort}");
    Console.WriteLine($"  Connection string: Endpoint=sb://localhost;SharedAccessKeyName=local;SharedAccessKey=not-a-real-key;Port={port}");
    Console.WriteLine();
    Console.WriteLine("Press Ctrl+C to stop.");

    await app.RunAsync(ct);
});

// --- send command ---
var sendEntityTypeArg = new Argument<string>("entityType") { Description = "Entity type: 'queue' or 'topic'" };
var sendEntityNameArg = new Argument<string>("entityName") { Description = "Name of the queue or topic" };
var sendBodyArg = new Argument<string>("body") { Description = "Message body (JSON string)" };
var sendApiUrlOption = new Option<string>("--api-url") { DefaultValueFactory = _ => "http://localhost:9090", Description = "REST API base URL" };

var sendCommand = new Command("send", "Send a message to a queue or topic");
sendCommand.Arguments.Add(sendEntityTypeArg);
sendCommand.Arguments.Add(sendEntityNameArg);
sendCommand.Arguments.Add(sendBodyArg);
sendCommand.Options.Add(sendApiUrlOption);
sendCommand.SetAction(async (parseResult, ct) =>
{
    var entityType = parseResult.GetValue(sendEntityTypeArg);
    var entityName = parseResult.GetValue(sendEntityNameArg);
    var body = parseResult.GetValue(sendBodyArg);
    var apiUrl = parseResult.GetValue(sendApiUrlOption);

    using var http = new HttpClient { BaseAddress = new Uri(apiUrl!) };
    var path = entityType?.ToLower() == "topic"
        ? $"/topics/{entityName}/messages"
        : $"/queues/{entityName}/messages";

    var response = await http.PostAsync(path, new StringContent(body!, Encoding.UTF8, "application/json"), ct);
    if (response.IsSuccessStatusCode)
        Console.WriteLine($"Message sent to {entityType} '{entityName}'");
    else
        Console.WriteLine($"Failed: {response.StatusCode}");
});

// --- list command ---
var listEntityTypeArg = new Argument<string>("entityType") { Description = "Entity type: 'queues' or 'topics'" };
var listApiUrlOption = new Option<string>("--api-url") { DefaultValueFactory = _ => "http://localhost:9090", Description = "REST API base URL" };

var listCommand = new Command("list", "List queues or topics");
listCommand.Arguments.Add(listEntityTypeArg);
listCommand.Options.Add(listApiUrlOption);
listCommand.SetAction(async (parseResult, ct) =>
{
    var entityType = parseResult.GetValue(listEntityTypeArg);
    var apiUrl = parseResult.GetValue(listApiUrlOption);

    using var http = new HttpClient { BaseAddress = new Uri(apiUrl!) };
    var path = entityType?.ToLower() == "topics" ? "/topics" : "/queues";
    var response = await http.GetStringAsync(path, ct);
    Console.WriteLine(response);
});

// --- peek command ---
var peekQueueArg = new Argument<string>("queueName") { Description = "Name of the queue to peek" };
var peekDlqOption = new Option<bool>("--deadletter") { Description = "Peek dead-letter messages instead" };
var peekApiUrlOption = new Option<string>("--api-url") { DefaultValueFactory = _ => "http://localhost:9090", Description = "REST API base URL" };

var peekCommand = new Command("peek", "Peek messages in a queue");
peekCommand.Arguments.Add(peekQueueArg);
peekCommand.Options.Add(peekDlqOption);
peekCommand.Options.Add(peekApiUrlOption);
peekCommand.SetAction(async (parseResult, ct) =>
{
    var queueName = parseResult.GetValue(peekQueueArg);
    var isDlq = parseResult.GetValue(peekDlqOption);
    var apiUrl = parseResult.GetValue(peekApiUrlOption);

    using var http = new HttpClient { BaseAddress = new Uri(apiUrl!) };
    var path = isDlq ? $"/queues/{queueName}/deadletter" : $"/queues/{queueName}";
    var response = await http.GetStringAsync(path, ct);
    Console.WriteLine(response);
});

// --- purge command ---
var purgeQueueArg = new Argument<string>("queueName") { Description = "Name of the queue to purge" };
var purgeApiUrlOption = new Option<string>("--api-url") { DefaultValueFactory = _ => "http://localhost:9090", Description = "REST API base URL" };

var purgeCommand = new Command("purge", "Purge all messages from a queue");
purgeCommand.Arguments.Add(purgeQueueArg);
purgeCommand.Options.Add(purgeApiUrlOption);
purgeCommand.SetAction(async (parseResult, ct) =>
{
    var queueName = parseResult.GetValue(purgeQueueArg);
    var apiUrl = parseResult.GetValue(purgeApiUrlOption);

    using var http = new HttpClient { BaseAddress = new Uri(apiUrl!) };
    var response = await http.DeleteAsync($"/queues/{queueName}/messages", ct);
    Console.WriteLine(response.IsSuccessStatusCode ? $"Queue '{queueName}' purged" : $"Failed: {response.StatusCode}");
});

// --- reset command ---
var resetApiUrlOption = new Option<string>("--api-url") { DefaultValueFactory = _ => "http://localhost:9090", Description = "REST API base URL" };

var resetCommand = new Command("reset", "Clear all emulator state");
resetCommand.Options.Add(resetApiUrlOption);
resetCommand.SetAction(async (parseResult, ct) =>
{
    var apiUrl = parseResult.GetValue(resetApiUrlOption);

    using var http = new HttpClient { BaseAddress = new Uri(apiUrl!) };
    var response = await http.PostAsync("/reset", null, ct);
    Console.WriteLine(response.IsSuccessStatusCode ? "All state cleared" : $"Failed: {response.StatusCode}");
});

// --- root command ---
var rootCommand = new RootCommand("LocalServiceBus — Azure Service Bus emulator for local development");
rootCommand.Subcommands.Add(startCommand);
rootCommand.Subcommands.Add(sendCommand);
rootCommand.Subcommands.Add(listCommand);
rootCommand.Subcommands.Add(peekCommand);
rootCommand.Subcommands.Add(purgeCommand);
rootCommand.Subcommands.Add(resetCommand);

return rootCommand.Parse(args).Invoke();
