using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SshZeroClient;

public class Program
{
    public static async Task Main()
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "sshzero.config.json");
        var cfg = LoadConfig(configPath);

        while (true)
        {
            //Console.Clear();
            Console.WriteLine("Pritunl Zero SSH Utility");
            Console.WriteLine("---------------------------------");
            if (cfg.Servers.Count == 0)
            {
                Console.WriteLine("Servers list is empty. Add to sshzero.config.json.");
                Console.WriteLine("Q. Exit");
            }
            else
            {
                for (int i = 0; i < cfg.Servers.Count; i++)
                {
                    var s = cfg.Servers[i];
                    Console.WriteLine($"{i + 1}. {s.Name} ({s.User}@{s.Host})");
                }
                Console.WriteLine("R. Renew certificate");
                Console.WriteLine("Q. Exit");
            }

            Console.Write("Select the server: ");
            var inp = Console.ReadLine();

            if (string.Equals(inp, "q", StringComparison.OrdinalIgnoreCase))
                return;

            if (string.Equals(inp, "r", StringComparison.OrdinalIgnoreCase))
            {
                await RequestNewCertificate(cfg);
                continue;
            }

            if (!int.TryParse(inp, out int idx) || idx < 1 || idx > cfg.Servers.Count)
                continue;

            var srv = cfg.Servers[idx - 1];

            try
            {
                await HandleServerAsync(cfg, srv);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Error: " + ex.Message);
                Console.ResetColor();
            }

            Console.WriteLine();
            Console.WriteLine("Press Enter to return to the menu...");
            Console.ReadLine();
        }
    }

    static AppConfig LoadConfig(string path)
    {
        Console.WriteLine("Loading config: " + path);

        if (!File.Exists(path))
        {
            var sample = new AppConfig
            {
                ZeroBaseUrl = "https://zero.company.com",
                UserKeyPath = "%USERPROFILE%/.ssh/id_ed25519",
                SshCommand = "ssh",
                SshCommandArgs = "{user}@{host}",
                SftpBrowserCommand = "SftpBrowser.exe",
                SftpBrowserArgs = "{user}@{host}",
                Servers =
                [
                    new ServerEntry
                    {
                        Name = "AppServer",
                        Host = "10.1.10.15",
                        User = "deployer"
                    }
                ]
            };

            var jsonSample = JsonSerializer.Serialize(sample, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(path, jsonSample);

            Console.WriteLine("Config created: " + path);
            Console.WriteLine("Edit the config and restart.");
            Environment.Exit(0);

            return null!; // unreachable, but avoids compiler warnings
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("ERROR: Failed to parse sshzero.config.json");
            Console.WriteLine(ex.Message);
            Console.ResetColor();

            Environment.Exit(1);
            return null!;
        }
    }


    static async Task HandleServerAsync(AppConfig cfg, ServerEntry srv)
    {
        Console.WriteLine($"Server: {srv.Name} ({srv.User}@{srv.Host})");
        if (CheckAndPrintCertificateValidity(cfg)) {
            ShowActionMenuAndRun(cfg, srv);
            return;
        };

        Console.WriteLine("Request for new certificate!");
        await RequestNewCertificate(cfg);
    }

    static bool CheckAndPrintCertificateValidity(AppConfig cfg)
    {
        var privateKeyPath = SshKeyHelper.Normalize(cfg.UserKeyPath);
        var certPubPath = privateKeyPath + "-cert.pub";

        var certInfo = SshKeyHelper.ParseCertificate(certPubPath);

        if (certInfo == null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;

            if (!File.Exists(certPubPath))
                Console.WriteLine("No SSH certificate found.");
            else
                Console.WriteLine("SSH certificate is unreadable or corrupted.");

            Console.ResetColor();
            return false;
        }

        if (!certInfo.IsValid)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("SSH certificate is EXPIRED.");
            Console.WriteLine($"Valid until: {certInfo.ValidTo.LocalDateTime}");
            Console.ResetColor();
            return false;
        }

        // Valid!
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Found valid SSH certificate.");
        Console.WriteLine($"Valid from : {certInfo.ValidFrom.LocalDateTime}");
        Console.WriteLine($"Valid until: {certInfo.ValidTo.LocalDateTime}");
        Console.ResetColor();

        return true;

    }
    static async Task RequestNewCertificate(AppConfig cfg){

        using var zero = new ZeroSshClient(cfg.ZeroBaseUrl);
        // 1) Ensure keypair
        var privateKeyPath = SshKeyHelper.Normalize(cfg.UserKeyPath);
        SshKeyHelper.EnsureKeyPair(privateKeyPath);
        var pubKey = SshKeyHelper.ReadPublicKey(privateKeyPath);

        Console.WriteLine("Opening browser for Pritunl Zero login...");
        zero.OpenBrowserForLogin();

        Console.Write("Press Enter after you finish login in the browser...");
        Console.ReadLine();

        Console.WriteLine();
        Console.WriteLine("Starting SSH challenge on Pritunl Zero...");
        var token = "";;
        try {
            token = await zero.StartChallengeAsync(pubKey);
        } catch (Exception ex){
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"{ex.GetType().Name}: {ex.Message}");
            Console.ResetColor();
            return;
        }

        Console.WriteLine("Opening browser to approve SSH certificate...");
        zero.OpenBrowserForToken(token);

        Console.WriteLine("Waiting for certificate from Pritunl Zero...");
        var certDoc = await zero.WaitForCertificateAsync(token, pubKey);
        if (certDoc is null)
        {
            Console.WriteLine("Can't get the certificate (timeout).");
            return;
        }

        var certPath = SshKeyHelper.SaveCertificate(certDoc, privateKeyPath);
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Certificate saved to: {certPath}");
        Console.ResetColor();
    }

    static void ShowActionMenuAndRun(AppConfig cfg, ServerEntry srv)
    {
        Console.WriteLine("Actions:");
        Console.WriteLine("  1) Open interactive SSH session");
        Console.WriteLine("  2) Open browser");
        Console.WriteLine("  3) Do both");
        Console.WriteLine("  Press Enter to do nothing");
        Console.Write("Select: ");

        var choice = Console.ReadLine()?.Trim();

        if (choice == "1")
        {
            Launcher.OpenSshShell(cfg, srv);
        }
        else if (choice == "2")
        {
            Launcher.OpenSftpBrowser(cfg, srv);
        }
        else if (choice == "3")
        {
            Launcher.OpenSftpBrowser(cfg, srv);
            Launcher.OpenSshShell(cfg, srv);
        }
    }

}

// ---------------- Моделі ----------------

public sealed class AppConfig
{
    public string ZeroBaseUrl { get; set; } = "";
    public string UserKeyPath { get; set; } = @"%USERPROFILE%\.ssh\id_ed25519";

    // що запускати для SSH:
    public string SshCommand { get; set; } = "ssh";
    public string SshCommandArgs { get; set; } = "{user}@{host}";

    // що запускати для SFTP-браузера:
    public string SftpBrowserCommand { get; set; } = "SftpBrowser.exe";
    public string SftpBrowserArgs { get; set; } = "{user}@{host}";
    public List<ServerEntry> Servers { get; set; } = new();
}

public sealed class ServerEntry
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public string User { get; set; } = "deployer";
}

public sealed class CertInfo
{
    public DateTime? ValidBefore { get; set; }
    public DateTime? ValidAfter { get; set; }
    public DateTime LastUpdated { get; set; }
}

public class SshCertificateInfo
{
    public DateTimeOffset ValidFrom { get; set; }
    public DateTimeOffset ValidTo { get; set; }
    public bool IsValid => DateTimeOffset.UtcNow >= ValidFrom && DateTimeOffset.UtcNow <= ValidTo;
}

// --------------- ZeroSshClient ---------------

public sealed class ZeroSshClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public ZeroSshClient(string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient();
    }

    public void Dispose()
    {
        _http.Dispose();
    }

    public async Task<string> StartChallengeAsync(string publicKey)
    {
        var payload = new { public_key = publicKey };
        using var resp = await _http.PostAsJsonAsync(_baseUrl + "/ssh/challenge", payload);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("token").GetString()
               ?? throw new Exception("token not found in response");
    }

    public void OpenBrowserForLogin()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = _baseUrl, // Наприклад: https://zero.company.com
            UseShellExecute = true
        });
    }


    public void OpenBrowserForToken(string token)
    {
        var url = $"{_baseUrl}/ssh?ssh-token={WebUtility.UrlEncode(token)}";
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    public async Task<JsonDocument?> WaitForCertificateAsync(string token, string publicKey,
        int maxTries = 10, int delayMs = 3000)
    {
        var payload = new { public_key = publicKey, token };

        for (var i = 0; i < maxTries; i++)
        {
            var req = new HttpRequestMessage(HttpMethod.Put, _baseUrl + "/ssh/challenge")
            {
                Content = JsonContent.Create(payload)
            };

            using var resp = await _http.SendAsync(req);

            if (resp.StatusCode == HttpStatusCode.ResetContent) // 205
            {
                await Task.Delay(delayMs);
                continue;
            }

            if (resp.StatusCode == HttpStatusCode.OK)
            {
                var json = await resp.Content.ReadAsStringAsync();
                return JsonDocument.Parse(json);
            }

            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound)
                throw new Exception($"SSH challenge failed: {resp.StatusCode}");

            resp.EnsureSuccessStatusCode();
        }

        return null;
    }
}

// --------------- SshKeyHelper ---------------


public static class SshKeyHelper
{
    private const string Keygen = "ssh-keygen";

    // ---------------- ХЕЛПЕР ДЛЯ ПРОЦЕСІВ ----------------

    private static (int code, string stdout, string stderr) Run(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Keygen,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"Cannot start {Keygen}");

        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();

        return (p.ExitCode, stdout, stderr);
    }

    public static string Normalize(string path) =>
        Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));

    public static string PubPath(string priv) => priv + ".pub";

    // ---------------- ГЕНЕРАЦІЯ КЛЮЧІВ ----------------

    public static void EnsureKeyPair(string privateKeyPath)
    {
        privateKeyPath = Normalize(privateKeyPath);
        var pub = PubPath(privateKeyPath);

        if (File.Exists(privateKeyPath) && File.Exists(pub))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(privateKeyPath)!);

        var (code, _, err) = Run($"-t ed25519 -f \"{privateKeyPath}\" -N \"\"");
        if (code != 0)
            throw new Exception($"ssh-keygen failed: {err}");
    }

    public static string ReadPublicKey(string privateKeyPath)
    {
        privateKeyPath = Normalize(privateKeyPath);
        var pub = PubPath(privateKeyPath);

        if (!File.Exists(pub))
            throw new FileNotFoundException("Public key not found", pub);

        return File.ReadAllText(pub).Trim();
    }

    // ---------------- СЕРТИФІКАТИ ----------------

    public static SshCertificateInfo? ParseCertificate(string certPath)
    {
        certPath = Normalize(certPath);
        if (!File.Exists(certPath))
            return null;

        var (code, stdout, stderr) = Run($"-Lf \"{certPath}\"");
        if (code != 0)
            throw new Exception($"ssh-keygen -Lf failed: {stderr}");

        var info = new SshCertificateInfo();

        foreach (var line in stdout.Split('\n'))
        {
            var l = line.Trim();
            if (!l.StartsWith("Valid:", StringComparison.OrdinalIgnoreCase))
                continue;

            // Формат:
            // Valid: from 2025-11-16T14:49:16 to 2025-11-17T00:52:16
            var parts = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5)
                return null;

            if (!DateTimeOffset.TryParse(parts[2], out var from))
                return null;

            if (!DateTimeOffset.TryParse(parts[4], out var to))
                return null;

            info.ValidFrom = from;
            info.ValidTo   = to;
        }

        if (info.ValidFrom == default || info.ValidTo == default)
            return null;

        return info;
    }

    public static bool IsCertificateValid(string certPath, out SshCertificateInfo? info)
    {
        info = ParseCertificate(certPath);
        return info?.IsValid ?? false;
    }

    public static string SaveCertificate(JsonDocument doc, string privateKeyPath)
    {
        privateKeyPath = Normalize(privateKeyPath);
        Directory.CreateDirectory(Path.GetDirectoryName(privateKeyPath)!);

        if (!doc.RootElement.TryGetProperty("certificates", out var arr) ||
            arr.ValueKind != JsonValueKind.Array ||
            arr.GetArrayLength() == 0)
        {
            throw new Exception("No certificates returned");
        }

        var certText = arr[0].GetString()
                      ?? throw new Exception("Empty certificate");

        var certPub = privateKeyPath + "-cert.pub";
        var certWin = privateKeyPath + "-cert";

        File.WriteAllText(certPub, certText + Environment.NewLine);
        File.WriteAllText(certWin, certText + Environment.NewLine);

        return certWin;
    }
}





// --------------- SFTP browser and ssh launcher---------------

public static class Launcher
{

    public static void OpenSshShell(AppConfig cfg, ServerEntry srv)
    {
        var cmd = string.IsNullOrWhiteSpace(cfg.SshCommand)
            ? "ssh"
            : cfg.SshCommand;

        var argsTemplate = string.IsNullOrWhiteSpace(cfg.SshCommandArgs)
            ? "{user}@{host}"
            : cfg.SshCommandArgs;

        var args = argsTemplate
            .Replace("{user}", srv.User)
            .Replace("{host}", srv.Host);

        var psi = new ProcessStartInfo
        {
            FileName = cmd,
            Arguments = args,
            UseShellExecute = true
        };

        try
        {
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Failed to start SSH client: " + ex.Message);
            Console.ResetColor();
        }
    }

    public static void OpenSftpBrowser(AppConfig cfg, ServerEntry srv)
    {
        var cmd = string.IsNullOrWhiteSpace(cfg.SftpBrowserCommand)
            ? "SftpBrowser.exe"
            : cfg.SftpBrowserCommand;

        var argsTemplate = string.IsNullOrWhiteSpace(cfg.SftpBrowserArgs)
            ? "{user}@{host}"
            : cfg.SftpBrowserArgs;

        var args = argsTemplate
            .Replace("{user}", srv.User)
            .Replace("{host}", srv.Host);

        Console.WriteLine("Opening SFTP browser...");

        var psi = new ProcessStartInfo
        {
            FileName = cmd,
            Arguments = args,
            UseShellExecute = true
        };

        try
        {
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Failed to start SFTP browser: " + ex.Message);
            Console.ResetColor();
        }
    }
}
