using System.Text;
using System.Text.Json;

// Deliberately accepts only a fixed set of test hostnames. It never starts a shell or uses the network.
Console.OutputEncoding = new UTF8Encoding(false);
switch (args.LastOrDefault())
{
    case "sleep.test":
        Console.WriteLine($"PID:{Environment.ProcessId}");
        await Console.Out.FlushAsync();
        await Task.Delay(TimeSpan.FromSeconds(60));
        return 0;
    case "flood.test":
        await Task.WhenAll(WriteFlood(Console.Out, 'o'), WriteFlood(Console.Error, 'e'));
        return 0;
    case "fail.test":
        Console.Error.WriteLine("Fixture intentional failure.");
        return 7;
    case "args.test":
        Console.WriteLine(JsonSerializer.Serialize(args));
        return 0;
    default:
        Console.Error.WriteLine("Unknown fixture mode.");
        return 2;
}

static async Task WriteFlood(TextWriter writer, char value)
{
    var chunk = new string(value, 2000);
    for (var i = 0; i < 100; i++) await writer.WriteAsync(chunk);
    await writer.FlushAsync();
}
