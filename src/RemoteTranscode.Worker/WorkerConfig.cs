using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemoteTranscode.Common;

namespace RemoteTranscode.Worker;

/// <summary>
/// Worker settings persisted as worker.json in <see cref="Directory"/>. One instance is shared by the UI and the
/// background loops; properties are plain auto-properties read without locks.
/// </summary>
public sealed class WorkerConfig
{
    public string Name { get; set; } = Environment.MachineName;
    public string? CoordinatorUrl { get; set; }
    public string? WorkerId { get; set; }

    /// <summary>Worker token encrypted with DPAPI for the current Windows user; never stored in clear.</summary>
    public string? ProtectedToken { get; set; }

    /// <summary>SHA-256 of the coordinator certificate, pinned at pairing.</summary>
    public string? CertificateFingerprint { get; set; }

    /// <summary>Empty: the FFmpeg bundled next to the executable (ffmpeg\ffmpeg.exe), else PATH.</summary>
    public string FfmpegPath { get; set; } = "";

    /// <summary>Path actually used, resolved at start; not saved so the worker folder can be moved.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ResolvedFfmpegPath { get; set; }

    public int MaxJobs { get; set; } = 2;
    /// <summary>User switch ("Disponible"); persisted, so a release or a manual switch-off survives a restart.</summary>
    public bool Available { get; set; } = true;
    /// <summary>
    /// Conversions on the GPU, off by default: faster, but a conversion resumed on the server would then mix two
    /// encoders in one file. Playback has its own switch (<see cref="HardwareEncodingForPlayback"/>).
    /// </summary>
    public bool HardwareEncodingForConversions { get; set; }

    /// <summary>
    /// Playback on the GPU too (off by default): much faster on a low-power CPU, at the cost of a stream that changes
    /// encoder when it moves to the server (the hand-over is still at the exact segment, the image may differ slightly).
    /// </summary>
    public bool HardwareEncodingForPlayback { get; set; }

    /// <summary>
    /// Video decoded by the GPU whenever a job is encoded on it (on by default): the CPU is left to the filters (HDR tone
    /// mapping, scaling, subtitles). Only codecs validated at start-up; pictures are identical to software decoding.
    /// </summary>
    public bool HardwareDecoding { get; set; } = true;

    /// <summary>
    /// HDR → SDR tone mapping on the GPU (libplacebo, on by default) when the start-up probe validated it: 2 to 2.4 times
    /// faster with a fraction of the CPU. Off: not announced to the coordinator (no conversion tone mapped on the GPU is
    /// sent here) and playback is tone mapped on the CPU. A change reconnects the worker.
    /// </summary>
    public bool GpuToneMapping { get; set; } = true;
    public AvailabilitySettings Availability { get; set; } = new();

    /// <summary>Configuration, logs and caches. EMBY_WORKER_HOME overrides it (several workers on one PC for tests).</summary>
    public static string Directory { get; } = Environment.GetEnvironmentVariable("EMBY_WORKER_HOME") is { Length: > 0 } home
        ? Path.GetFullPath(home)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EmbyRemoteWorker");
    public static string FilePath => Path.Combine(Directory, "worker.json");
    public static string CaFilePath => Path.Combine(Directory, "coordinator.pem");
    public static string JobsDirectory => Path.Combine(Directory, "jobs");
    public static string LogsDirectory => Path.Combine(Directory, "logs");

    private static string BackupPath => FilePath + ".bak";

    // Save is called from the UI thread, the release thread and the availability switch: one writer at a time.
    private static readonly object saveGate = new();

    /// <summary>
    /// Never throws: a damaged worker.json (e.g. power cut on an older version) falls back to the previous copy kept by
    /// <see cref="Save"/>, then to defaults (the worker must then be paired again), with the reason logged.
    /// </summary>
    public static WorkerConfig Load(Action<string>? log = null)
    {
        foreach (string path in new[] { FilePath, BackupPath })
        {
            try
            {
                if (!File.Exists(path))
                    continue;
                var config = JsonSerializer.Deserialize<WorkerConfig>(File.ReadAllText(path));
                if (config is null)
                    continue;
                if (path == BackupPath)
                    log?.Invoke($"worker.json illisible : configuration reprise de la copie de sauvegarde {BackupPath}");
                return config;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                log?.Invoke($"configuration illisible ({path}) : {ex.Message}");
            }
        }
        if (File.Exists(FilePath) || File.Exists(BackupPath))
            log?.Invoke("configuration par défaut utilisée : association à refaire dans les paramètres");
        return new();
    }

    /// <summary>
    /// Atomic: the new content is written to a temporary file, then swapped in, the previous version being kept as
    /// worker.json.bak. A crash mid-save leaves a complete file either way. I/O errors propagate to the caller.
    /// </summary>
    public void Save()
    {
        lock (saveGate)
        {
            System.IO.Directory.CreateDirectory(Directory);
            string temp = FilePath + ".tmp";
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })));
                // On disk before the rename, or a power cut could leave the renamed file empty.
                fs.Flush(flushToDisk: true);
            }
            if (File.Exists(FilePath))
                File.Replace(temp, FilePath, BackupPath, ignoreMetadataErrors: true);
            else
                File.Move(temp, FilePath, overwrite: true);
        }
    }

    // DPAPI CurrentUser scope: worker.json copied to another account or PC cannot be used to impersonate this worker
    // (and the token must be re-paired there).
    public string? GetToken() =>
        ProtectedToken is null ? null : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ProtectedToken), null, DataProtectionScope.CurrentUser));

    public void SetToken(string token) =>
        ProtectedToken = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));

    /// <summary>Applies a pairing code (shown on the plugin page, or printed by the standalone coordinator's "pair").</summary>
    /// <remarks>
    /// Everything is validated before anything is assigned, so an invalid code leaves the current pairing intact; any
    /// invalid content throws <see cref="FormatException"/>.
    /// </remarks>
    public void ApplyPairingCode(string code)
    {
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(code.Trim())));
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException("code d'association invalide");
        string url = Required(root, "url"), id = Required(root, "id"), token = Required(root, "token");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new FormatException("adresse du coordinateur invalide");
        string? fingerprint = null;
        if (root.TryGetProperty("fingerprint", out var fp) && fp.ValueKind != JsonValueKind.Null)
            fingerprint = fp.ValueKind == JsonValueKind.String ? fp.GetString() : throw new FormatException("empreinte invalide");

        CoordinatorUrl = url;
        WorkerId = id;
        // No fingerprint: the coordinator is behind a reverse proxy with a publicly trusted certificate.
        CertificateFingerprint = fingerprint;
        SetToken(token);

        static string Required(JsonElement root, string name) =>
            root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s
                ? s
                : throw new FormatException($"champ « {name} » manquant dans le code d'association");
    }
}
