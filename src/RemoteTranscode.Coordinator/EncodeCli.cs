using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace RemoteTranscode.Coordinator;

public sealed record PairRequest(string? Name);

/// <summary>"coordinator encode ..." commands: thin client of the running service's loopback admin API.</summary>
public static class EncodeCli
{
    /// <summary>JSON shape of a task for the admin API, the status page and "encode list". Call under the queue's lock (<see cref="EncodeQueue.Summaries"/>).</summary>
    public static object Summary(EncodeTask t) => new
    {
        t.Id,
        Status = t.Status.ToString(),
        t.Profile,
        t.Input,
        t.Output,
        t.Percent,
        t.Speed,
        t.Worker,
        Chunks = t.Chunks.Count,
        Slices = t.Slices.Count,
        SlicesDone = t.Slices.Count(s => s.Done),
        RemainingSeconds = t.RemainingSeconds(),
        // Who encodes which part of the film right now (times in the film).
        Runs = t.Slices.Where(s => s.IsRunning && s.Worker != EncodeQueue.OfferMarker).OrderBy(s => s.Index).Select(s => new
        {
            Slice = s.Index + 1,
            s.Worker,
            s.Start,
            End = s.Last ? t.Source?.DurationSeconds ?? s.End : s.End,
            Position = Math.Max(s.Start, s.Position),
            s.Speed,
        }).ToList(),
        t.Attempts,
        // HDR → SDR of the whole conversion ("Libplacebo": capable workers only, "Zscale"); null without HDR.
        ToneMapping = Common.EmbyFilters.NeedsToneMapping(t.Requirements.Filters) ? t.Requirements.ToneMapping.ToString() : null,
        // False while no slice has started: the tone mapper above may still change (see ToneMappingDecision).
        ToneMappingDecided = Common.EmbyFilters.NeedsToneMapping(t.Requirements.Filters) ? t.ToneMappingFinal || !t.IsActive : (bool?)null,
        t.Audio,
        t.Note,
        t.Error,
        t.CreatedAt,
        t.UpdatedAt,
    };

    public static async Task<int> RunAsync(CoordinatorOptions options, string[] args)
    {
        using var http = new HttpClient { BaseAddress = new Uri(options.ShimUrl), Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", File.ReadAllText(options.AdminSecretPath).Trim());
        string verb = args.FirstOrDefault() ?? "list";
        // Conversions are started from Emby (its own quality profiles); the command line only shows them.
        HttpResponseMessage response = verb switch
        {
            "list" => await http.GetAsync("/admin/encodes"),
            _ => throw new ArgumentException("encode list"),
        };
        string body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"{(int)response.StatusCode}: {body}");
            return 1;
        }
        if (verb == "list")
        {
            foreach (var t in JsonDocument.Parse(body).RootElement.EnumerateArray())
            {
                Console.WriteLine($"{t.GetProperty("id").GetString()}  {t.GetProperty("status").GetString(),-10} {t.GetProperty("percent").GetDouble(),5:0.0}%  " +
                    $"{t.GetProperty("profile").GetString(),-11} morceaux {t.GetProperty("chunks").GetInt32(),3}  {t.GetProperty("worker").GetString() ?? "-",-14} " +
                    $"{Path.GetFileName(t.GetProperty("input").GetString())}");
                string? note = t.GetProperty("error").GetString() ?? t.GetProperty("note").GetString();
                if (note != null)
                    Console.WriteLine($"              {note}");
            }
        }
        else if (body.Length > 0)
        {
            Console.WriteLine(body);
        }
        return 0;
    }

    /// <summary>Secret of the command-line admin client, created on first start and readable by the service account only.</summary>
    public static string LoadOrCreateAdminSecret(CoordinatorOptions options)
    {
        // Only the account running the coordinator may administer it (owner-only from creation).
        return CoordinatorCertificate.LoadOrCreateSecret(options.AdminSecretPath);
    }
}
