using System.Text.Json;
using JPSoftworks.MediaControlsExtension.Media.Browser;
using JPSoftworks.MediaControlsExtension.Media.Gsmtc;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Media.Control;

if (Environment.UserName != "WDAGUtilityAccount") throw new InvalidOperationException("Run only inside Windows Sandbox.");
var browser = new BrowserBackend();
var registry = new MediaBackendRegistry()
    .Register(new("browser.companion", "Browser", "", _ => browser, true))
    .Register(new("gsmtc", "Windows", "", _ => new GsmtcBackend(NullLogger<GsmtcBackend>.Instance), true))
    .Register(new("gsmtc.worker", "Windows worker", "", _ => throw new InvalidOperationException()));
await using var composite = new CompositeMediaBackend(registry);
await composite.StartAsync(CancellationToken.None);
var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
Console.WriteLine("{\"ready\":true}");
while (await Console.In.ReadLineAsync() is { } line && line != "quit")
{
    try
    {
        var snapshot = await composite.ReadSnapshotAsync(CancellationToken.None);
        var leaf = await browser.ReadSnapshotAsync(CancellationToken.None);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            utc = DateTime.UtcNow,
            raw = manager.GetSessions().Select(session => new
            {
                applicationId = session.SourceAppUserModelId,
                playback = session.GetPlaybackInfo().PlaybackStatus.ToString()
            }),
            claims = leaf.ReplacesSources.IsDefault ? [] : leaf.ReplacesSources.ToArray(),
            sessions = snapshot.Sessions.Select(session => new
            {
                id = session.Id.Value,
                session.BindingGeneration,
                source = session.MediaProperties.Source,
                title = session.MediaProperties.Title,
                playback = session.PlaybackState.ToString(),
                session.Origin,
                session.IsAvailable
            }),
            backends = snapshot.Backends
        }));
    }
    catch (Exception error) { Console.WriteLine(JsonSerializer.Serialize(new { error = error.ToString() })); }
}