using System.IO.Pipes;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JPSoftworks.MediaControlsExtension.BrowserIntegration;

[assembly: SupportedOSPlatform("windows")]

namespace BrowserCompanion;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args is ["--register-browser-integration"] or ["--register-browser-integration", _] or ["--remove-browser-integration"])
            {
                await PackagedBrowserIntegration.RunAsync(AppContext.BaseDirectory, args[0] == "--remove-browser-integration",
                    args.Length > 1 ? args[1] : string.Empty, CancellationToken.None);
                Console.WriteLine("Browser integration updated.");
                return 0;
            }

            var identity = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("No user SID.");
            var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
            var pipeName = $"JPSoftworks.BrowserMedia.Spike.{suffix}";
            var instance = Environment.GetEnvironmentVariable("BROWSER_MEDIA_SPIKE_INSTANCE");
            if (!string.IsNullOrEmpty(instance))
            {
                if (!Guid.TryParse(instance, out var instanceId))
                {
                    throw new InvalidOperationException("BROWSER_MEDIA_SPIKE_INSTANCE must be a GUID.");
                }

                pipeName += $".{instanceId:N}";
            }

            if (args is ["--pipe-name"])
            {
                Console.WriteLine(pipeName);
                return 0;
            }

            if (args is ["--service" or "--service-on-demand"])
            {
                var onDemand = args[0] == "--service-on-demand";
                if (onDemand) Console.SetError(TextWriter.Null);
                using var singleton = new Mutex(true, $"Local\\{pipeName}", out var created);
                if (!created)
                {
                    if (onDemand) return 0;
                    throw new InvalidOperationException("The browser companion service is already running.");
                }

                if (!onDemand) Console.Error.WriteLine($"Listening on {pipeName}. Press Ctrl+C to stop.");
                await new MediaServer().RunAsync(pipeName, onDemand ? TimeSpan.FromSeconds(30) : null);
                return 0;
            }

            var firefoxManifest = PackagedBrowserIntegration.IsPackaged(AppContext.BaseDirectory)
                ? PackagedBrowserIntegration.FirefoxManifestPath : Path.Combine(AppContext.BaseDirectory, "firefox-native-host.json");
            var firefox = args.Length == 2 && string.Equals(args[0], firefoxManifest, StringComparison.OrdinalIgnoreCase);
            if (firefox || args.Length > 0 && args[0].StartsWith("chrome-extension://", StringComparison.Ordinal))
            {
                var manifestPath = firefox ? firefoxManifest : PackagedBrowserIntegration.IsPackaged(AppContext.BaseDirectory)
                    ? PackagedBrowserIntegration.ManifestPath : Path.Combine(AppContext.BaseDirectory, "native-host.json");
                var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!;
                if (manifest[firefox ? "allowed_extensions" : "allowed_origins"] is not JsonArray origins ||
                    !origins.Any(item => item?.GetValue<string>() == args[firefox ? 1 : 0]))
                {
                    throw new InvalidOperationException("Extension origin is not registered for this host.");
                }

                await BridgeAsync(pipeName);
                return 0;
            }

            JsonObject request;
            if (args is ["--list"])
            {
                request = new JsonObject { ["type"] = "list" };
            }
            else if (args.Length is 4 or 5 && args[0] == "--command")
            {
                request = new JsonObject
                {
                    ["type"] = "command", ["connectionId"] = args[1], ["sessionId"] = args[2], ["operation"] = args[3],
                    ["value"] = args.Length == 5 ? JsonNode.Parse(args[4]) : null
                };
            }
            else
            {
                Console.Error.WriteLine("Usage: BrowserCompanion --service | --list | --command CONNECTION SESSION OPERATION [JSON_VALUE]");
                return 2;
            }

            await using var pipe = await ConnectAsync(pipeName);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (Wire.Text(request, "operation") is "play" or "activate")
                request["allowForeground"] = ForegroundPermission.AllowRelay(pipe);
            await Wire.WriteAsync(pipe, request, timeout.Token);
            var response = await Wire.ReadAsync(pipe, timeout.Token) ?? throw new EndOfStreamException();
            Console.WriteLine(response.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return response["status"] is null || Wire.Text(response, "status") == "completed" ? 0 : 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(string pipeName, int timeoutMilliseconds = 1500)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(timeoutMilliseconds);
            return pipe;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    private static async Task BridgeAsync(string pipeName)
    {
        using var browser = ForegroundPermission.FindBrowser();
        await using var pipe = await ConnectOrStartAsync(pipeName);
        using var cancellation = new CancellationTokenSource();
        var identity = BrowserIdentity.Describe(browser);
        await Wire.WriteAsync(pipe, new JsonObject
        {
            ["type"] = "browser", ["applicationId"] = BrowserIdentity.Resolve(browser),
            ["browserName"] = identity.Name, ["browserExecutable"] = identity.ExecutablePath
        });
        var fromBrowser = ForwardAsync(Console.OpenStandardInput(), pipe, cancellation.Token);
        var toBrowser = ForwardAsync(pipe, Console.OpenStandardOutput(), cancellation.Token, true, browser);
        var finished = await Task.WhenAny(fromBrowser, toBrowser);
        await cancellation.CancelAsync();
        await finished;
    }

    private static async Task<NamedPipeClientStream> ConnectOrStartAsync(string pipeName)
    {
        try { return await ConnectAsync(pipeName, 250); }
        catch (TimeoutException)
        {
            using var service = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the companion executable."),
                Arguments = "--service-on-demand",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            }) ?? throw new InvalidOperationException("Cannot start the companion service.");
            return await ConnectAsync(pipeName, 5000);
        }
    }

    private static async Task ForwardAsync(Stream source, Stream destination, CancellationToken cancellationToken,
        bool transferForeground = false, System.Diagnostics.Process? browser = null)
    {
        while (await Wire.ReadAsync(source, cancellationToken) is { } message)
        {
            if (transferForeground && Wire.Text(message, "type") == "command")
            {
                var received = message["allowForeground"]?.GetValue<bool>() == true;
                message["allowForeground"] = Wire.Text(message, "operation") is "play" or "activate" &&
                    message["allowForeground"]?.GetValue<bool>() == true &&
                    message["deadline"]?.GetValue<long>() > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() &&
                    ForegroundPermission.AllowBrowser(browser);
                ForegroundPermission.Trace("bridge", message, received);
            }
            if (!transferForeground && Wire.Text(message, "type") == "result")
                ForegroundPermission.Trace("result", message);
            await Wire.WriteAsync(destination, message, cancellationToken);
        }
    }
}