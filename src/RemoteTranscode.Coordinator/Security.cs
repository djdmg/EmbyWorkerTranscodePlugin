using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace RemoteTranscode.Coordinator;

/// <summary>A paired worker as stored in workers.json (only the SHA-256 of its token is kept).</summary>
public sealed record PairedWorker(string Id, string Name, string TokenHash, DateTimeOffset CreatedAt, bool Revoked);

/// <summary>Worker identities: each worker holds a random token, the coordinator only stores its SHA-256.</summary>
public sealed class WorkerStore(CoordinatorOptions options)
{
    // In-process only: the "pair"/"revoke" command-line verbs run in another process and are not serialized with a
    // running service (the atomic rename keeps the file valid, but one of two concurrent writes can be lost).
    private readonly object gate = new();

    public IReadOnlyList<PairedWorker> Load()
    {
        lock (gate)
        {
            if (!File.Exists(options.WorkersPath))
                return [];
            return JsonSerializer.Deserialize<List<PairedWorker>>(File.ReadAllText(options.WorkersPath)) ?? [];
        }
    }

    public (PairedWorker Worker, string Token) Pair(string name)
    {
        lock (gate)
        {
            var workers = Load().ToList();
            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var worker = new PairedWorker(Guid.NewGuid().ToString("N")[..12], name, Hash(token), DateTimeOffset.UtcNow, false);
            workers.Add(worker);
            Save(workers);
            return (worker, token);
        }
    }

    /// <summary>Stores the name a worker announces (set in its own settings); returns the updated record, or null.</summary>
    public PairedWorker? Rename(string id, string name)
    {
        lock (gate)
        {
            var workers = Load().ToList();
            int i = workers.FindIndex(w => w.Id == id);
            if (i < 0 || workers[i].Name == name)
                return null;
            workers[i] = workers[i] with { Name = name };
            Save(workers);
            return workers[i];
        }
    }

    /// <summary>Revokes the worker with this id or name; returns the id(s) revoked (empty if none matched).</summary>
    public IReadOnlyList<string> Revoke(string idOrName)
    {
        lock (gate)
        {
            var workers = Load().ToList();
            int i = workers.FindIndex(w => w.Id == idOrName || w.Name == idOrName);
            if (i < 0)
                return [];
            workers[i] = workers[i] with { Revoked = true };
            Save(workers);
            return [workers[i].Id];
        }
    }

    /// <summary>
    /// Finds the non-revoked worker owning this token. Hashes are compared in constant time; the file is read on every
    /// call so a revocation made from the command line applies to the next connection.
    /// </summary>
    public PairedWorker? Authenticate(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return null;
        string hash = Hash(token);
        return Load().FirstOrDefault(w => !w.Revoked && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(w.TokenHash), Encoding.ASCII.GetBytes(hash)));
    }

    private void Save(List<PairedWorker> workers)
    {
        Directory.CreateDirectory(options.DataDir);
        string tmp = options.WorkersPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(workers, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, options.WorkersPath, overwrite: true);
    }

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

public static class CoordinatorCertificate
{
    /// <summary>Loads the coordinator's self-signed certificate, creating it on first start.</summary>
    public static X509Certificate2 LoadOrCreate(CoordinatorOptions options)
    {
        if (File.Exists(options.CertificatePath))
        {
            TryRestrict(options.CertificatePath);
            return new X509Certificate2(options.CertificatePath, (string?)null, X509KeyStorageFlags.Exportable);
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Emby Remote Transcode Coordinator", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        foreach (string name in options.CertificateNames)
        {
            if (IPAddress.TryParse(name, out var ip))
                san.AddIpAddress(ip);
            else
                san.AddDnsName(name);
        }
        request.CertificateExtensions.Add(san.Build());
        // Self-signed and used as its own trust anchor by the workers (pinned), so it is marked as a CA.
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyCertSign, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));

        Directory.CreateDirectory(options.DataDir);
        // The PFX holds the private key: owner-only from creation. The PEM is the public certificate only.
        WriteRestricted(options.CertificatePath, cert.Export(X509ContentType.Pfx));
        File.WriteAllText(options.CertificatePemPath, cert.ExportCertificatePem());
        return new X509Certificate2(options.CertificatePath, (string?)null, X509KeyStorageFlags.Exportable);
    }

    /// <summary>SHA-256 of the DER certificate, as pinned by workers paired without a public URL.</summary>
    public static string Fingerprint(X509Certificate2 cert) => Convert.ToHexString(SHA256.HashData(cert.RawData));

    /// <summary>
    /// Reads a random secret file, creating it owner-only (0600) on first start. An existing file is tightened too, so
    /// files left group/world-readable by an older version stop being readable by other accounts.
    /// </summary>
    public static string LoadOrCreateSecret(string path)
    {
        if (File.Exists(path))
        {
            TryRestrict(path);
            return File.ReadAllText(path).Trim();
        }
        string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        WriteRestricted(path, Encoding.ASCII.GetBytes(secret));
        return secret;
    }

    /// <summary>
    /// Writes a file that is owner-only on Unix from the moment it exists (no window with the umask's permissions);
    /// an existing file keeps its inode, so its mode is tightened explicitly as well.
    /// </summary>
    public static void WriteRestricted(string path, byte[] content)
    {
        var fso = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        // Setting UnixCreateMode at all throws on Windows.
        if (!OperatingSystem.IsWindows())
            fso.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var fs = new FileStream(path, fso))
            fs.Write(content);
        TryRestrict(path);
    }

    /// <summary>Owner-only access on Unix; best-effort (the mode of a file owned by another account cannot be changed).</summary>
    public static void TryRestrict(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Not the owner (e.g. created by another account): leave it; it still works, just less tightly.
        }
    }
}
