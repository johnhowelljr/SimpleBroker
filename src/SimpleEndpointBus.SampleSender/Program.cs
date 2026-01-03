using SimpleEndpointBus.Client;

if (args.Length < 2)
{
    Console.WriteLine("Usage: dotnet run --project src/SimpleEndpointBus.SampleSender -- <endpointId> <message> [holdSeconds]");
    Console.WriteLine("Example: dotnet run --project src/SimpleEndpointBus.SampleSender -- test.endpoint \"hello\"");
    Environment.Exit(2);
}

var endpointId = args[0];
var message = args[1];
var holdSeconds = 15.0;

if (args.Length >= 3 && double.TryParse(args[2], out var hs))
    holdSeconds = hs;

Console.WriteLine($"Sending to endpointId='{endpointId}' holdSeconds={holdSeconds} ...");

await EndpointBusClient.SendAsync(endpointId, message, holdSeconds);

Console.WriteLine("Sent OK.");
