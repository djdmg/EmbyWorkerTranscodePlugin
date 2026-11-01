using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using RemoteTranscode.Common;
using RemoteTranscode.Coordinator;

// Usage:
//   coordinator [--config coordinator.json]                 run the service
//   coordinator pair <worker name> [--config ...]           create a worker identity and print its pairing code
//   coordinator revoke <worker id or name> [--config ...]   revoke a worker
//   coordinator workers [--config ...]                      list paired workers
//   coordinator encode list [--config ...]                  Emby conversions handled by the workers

string configPath = ArgValue(args, "--config") ?? Path.Combine(AppContext.BaseDirectory, "coordinator.json");
var options = File.Exists(configPath)
    ? JsonSerializer.Deserialize<CoordinatorOptions>(File.ReadAllText(configPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip })!
    : new CoordinatorOptions();
if (!Path.IsPathRooted(options.DataDir))
    options.DataDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, options.DataDir);

// The command-line verbs write the same files as a running service (WorkerStore's lock is per process).
var store = new WorkerStore(options);
var certificate = CoordinatorCertificate.LoadOrCreate(options);
string command = args.FirstOrDefault(a => !a.StartsWith("--") && a != ArgValue(args, "--config")) ?? "run";

switch (command)
{
    case "pair":
    {
        string name = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--")) ?? throw new ArgumentException("pair <worker name>");
        var (worker, token) = store.Pair(name);
        string code = CoordinatorHost.PairingCode(options, certificate, worker, token);
        Console.WriteLine($"Worker '{name}' paired with id {worker.Id}.");
        Console.WriteLine("Pairing code (enter it in the worker, it contains a secret):");
        Console.WriteLine(code);
        return 0;
    }
    case "revoke":
    {
        string id = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--")) ?? throw new ArgumentException("revoke <worker id or name>");
        Console.WriteLine(store.Revoke(id).Count > 0 ? $"Revoked {id}. Its next connection will be refused." : $"Unknown worker {id}.");
        return 0;
    }
    case "workers":
        foreach (var w in store.Load())
            Console.WriteLine($"{w.Id}  {w.Name,-20} {(w.Revoked ? "revoked" : "active")}  {w.CreatedAt:u}");
        return 0;
    case "encode":
        return await EncodeCli.RunAsync(options, args.SkipWhile(a => a != "encode").Skip(1).Where(a => a != "--config" && a != ArgValue(args, "--config")).ToArray());
}

var app = CoordinatorHost.Build(options, logging => logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "yyyy-MM-dd HH:mm:ss "; }));
// The host uses EmbeddedLifetime (no signal handling of its own, as inside Emby): standalone, Ctrl+C and SIGTERM
// stop the application so ApplicationStopping fires (background loops end, local ffmpeg killed) before exit.
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    app.Lifetime.StopApplication();
};
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
{
    ctx.Cancel = true;
    app.Lifetime.StopApplication();
});
app.Run();
return 0;

static string? ArgValue(string[] args, string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
