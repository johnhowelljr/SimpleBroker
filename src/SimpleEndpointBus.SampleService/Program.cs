using SimpleEndpointBus.Client;

var endpointId = Environment.GetEnvironmentVariable("ENDPOINT_ID") ?? "test.endpoint";

Console.WriteLine("Starting sample service...");
Console.WriteLine($"ENDPOINT_ID={endpointId}");
Console.WriteLine("Tip: if broker is on a different subnet, set SEB_Peers=http://x.x.x.x:8080");

var sub = await EndpointBusClient.RegisterAsync(
    endpointId,
    msg =>
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] endpoint={msg.EndpointId} trace={msg.TraceId ?? "-"} msg={msg.Message}");
        return Task.CompletedTask;
    });

Console.WriteLine("Registered. Press ENTER to quit.");
Console.ReadLine();

sub.Dispose();
