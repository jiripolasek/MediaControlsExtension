// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using JPSoftworks.MediaControlsExtension.Media.ITunes;
using JPSoftworks.MediaControlsExtension.Media.ITunes.Interop;
using Windows.System;

namespace JPSoftworks.MediaControlsExtension.Media.Tests.Infrastructure;

internal sealed class ITunesNativeFixture : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly List<NativeObject> _objects = [];
    private readonly List<string> _callsAfterQuit = [];
    private readonly List<string> _lifetimeErrors = [];
    private readonly List<string> _exportedFiles = [];
    private readonly TaskCompletionSource _appReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int _eventId;
    private DispatcherQueue _queue = null!;
    private nint _sink;
    private int _ownerThread;
    private int _nativeDepth;
    private bool _quitSent;

    private ITunesNativeFixture(int eventId, Func<string, string, CancellationToken, Task<bool>>? activateSource)
    {
        this._eventId = eventId;
        this.Backend = new(isProcessRunning: _ =>
        {
            this.DiscoveryChecks++;
            return false;
        }, activateSource: activateSource);
    }

    public ITunesBackend Backend { get; }
    public int DiscoveryChecks { get; private set; }
    public string? QuitDuring { get; set; }
    public int StateReadResult { get; set; }
    public int TrackReadResult { get; set; }
    public ITPlayerState PlayerState { get; set; } = ITPlayerState.Playing;
    public int CommandResult { get; set; }
    public bool ApplyPlaybackCommands { get; set; } = true;
    public bool HasCurrentTrack { get; set; } = true;
    public bool ShuffleEnabled { get; private set; }
    public int RepeatMode { get; private set; }

    public static async Task<ITunesNativeFixture> CreateAsync(
        int eventId = 9, Func<string, string, CancellationToken, Task<bool>>? activateSource = null)
    {
        var fixture = new ITunesNativeFixture(eventId, activateSource);
        await fixture.Backend.StartAsync(default);
        fixture._queue = (DispatcherQueue)fixture.GetBackendField("_dispatcherQueue")!;
        await fixture.RunAsync(fixture.Connect);
        return fixture;
    }

    public object? GetBackendField(string name) =>
        typeof(ITunesBackend).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this.Backend);

    private void SetBackendField(string name, object? value) =>
        typeof(ITunesBackend).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this.Backend, value);

    public void InvokeBackend(string name, params object[] arguments) =>
        typeof(ITunesBackend).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this.Backend, arguments);

    public Task RefreshAsync() => this.RunAsync(() => this.InvokeBackend("UpdatePlaybackAndMetadata"));

    public Task SetExecutablePathAsync(string? path) => this.RunAsync(() => this.SetBackendField("_executablePath", path));

    public async Task SendPlaybackEventAsync(ITunesEventDispId eventId)
    {
        await this.RunAsync(() => ITunesNative.DispatchInvoke(this._sink, (int)eventId));
        await this.RunAsync(static () => { });
    }

    public Task<MediaBackendCommandResult> ExecuteAsync(MediaOperation operation) =>
        this.Backend.ExecuteAsync(new(new(1), (long)this.GetBackendField("_bindingGeneration")!, operation, []), default);

    public void RequestRefresh() =>
        this.Backend.InvalidateObservations([new(new(1), MediaBackendObservationChanges.Playback)]);

    public async Task RunAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.IsTrue(this._queue.TryEnqueue(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }));
        await completion.Task.WaitAsync(Timeout);
    }

    public void SendQuit()
    {
        this._quitSent = true;
        if (this._sink != 0)
        {
            _ = ITunesNative.DispatchInvoke(this._sink, this._eventId);
        }
    }

    public async Task AssertQuitCompletedAsync()
    {
        await this._appReleased.Task.WaitAsync(Timeout);
        await this.RunAsync(static () => { });
        Assert.IsEmpty(this._callsAfterQuit, string.Join(", ", this._callsAfterQuit));
        Assert.IsEmpty(this._lifetimeErrors, string.Join(", ", this._lifetimeErrors));
        Assert.IsTrue(this._objects.All(static item => item.References == 0));
        Assert.IsTrue(((ITunesProcessExitSubscription)this.GetBackendField("_processExitSubscription")!).IsAttached);
        Assert.AreEqual(Environment.ProcessId, this.GetBackendField("_quittingProcessId"));
        Assert.AreEqual(false, this.GetBackendField("_isDiscoveryTimerRunning"));
        Assert.IsTrue(this._exportedFiles.All(static path => !File.Exists(path)));
        var snapshot = await this.Backend.ReadSnapshotAsync(default);
        Assert.AreEqual(MediaBackendConnectionState.Disconnected, snapshot.Connection);
        Assert.IsEmpty(snapshot.Sessions);
    }

    public void AssertTemporaryObjectsReleased()
    {
        Assert.IsTrue(this._objects.Where(static item => item.Name is not ("App" or "ConnectionPoint"))
            .All(static item => item.References == 0));
        Assert.IsEmpty(this._lifetimeErrors);
    }

    private void Connect()
    {
        this._ownerThread = Environment.CurrentManagedThreadId;
        var app = this.AddObject("App", 64, 1);
        var connectionPoint = this.AddObject("ConnectionPoint", 7, 1);
        var playlist = this.AddObject("Playlist", 27);
        var track = this.AddObject("Track", 72);
        var artworks = this.AddObject("Artworks", 9);
        var artwork = this.AddObject("Artwork", 11);

        foreach (var (slot, name) in new[] { (7, "BackTrack"), (9, "NextTrack"), (10, "Pause"), (11, "Play"), (17, "Stop") })
        {
            app.SetMethod<Command>(slot, _ =>
            {
                this.Call(name);
                if (this.CommandResult >= 0 && this.ApplyPlaybackCommands)
                {
                    this.PlayerState = name switch
                    {
                        "Play" => ITPlayerState.Playing,
                        "Pause" or "Stop" => ITPlayerState.Stopped,
                        _ => this.PlayerState,
                    };
                    if (name == "Stop")
                    {
                        this.HasCurrentTrack = false;
                    }
                }

                return this.CommandResult;
            });
        }

        this.AddNumber(app, 39, "PlayerState", () => (int)this.PlayerState);
        this.AddNumber(app, 40, "PlayerPosition", () => 30);
        app.SetMethod<GetPointer>(62, (nint _, out nint value) =>
        {
            value = this.HasCurrentTrack ? track.Acquire() : 0;
            this.Call("CurrentTrack");
            return this.TrackReadResult;
        });
        this.AddObjectGetter(app, 63, "CurrentPlaylist", playlist);
        playlist.SetMethod<GetBoolean>(22, (nint _, out short value) =>
        {
            value = this.ShuffleEnabled ? (short)-1 : (short)0;
            return this.Call("Playlist.Shuffle");
        });
        playlist.SetMethod<SetBoolean>(23, (_, value) =>
        {
            this.ShuffleEnabled = value != 0;
            return this.Call("Playlist.SetShuffle");
        });
        this.AddNumber(playlist, 25, "Playlist.Repeat", () => this.RepeatMode);
        playlist.SetMethod<SetNumber>(26, (_, value) =>
        {
            this.RepeatMode = value;
            return this.Call("Playlist.SetRepeat");
        });

        foreach (var (slot, name) in new[] { (8, "Name"), (20, "Album"), (22, "Artist"), (45, "Genre") })
        {
            track.SetMethod<GetPointer>(slot, (nint _, out nint value) =>
            {
                value = Marshal.StringToBSTR("Track");
                return this.Call($"Track.{name}");
            });
        }

        foreach (var (slot, name) in new[] { (14, "DatabaseId"), (38, "Duration"), (63, "Count"), (65, "Number") })
        {
            this.AddNumber(track, slot, $"Track.{name}", () => 1);
        }

        this.AddObjectGetter(track, 71, "Track.Artwork", artworks);
        artworks.SetMethod<GetItem>(8, (nint _, int index, out nint value) =>
        {
            value = artwork.Acquire();
            return this.Call("Artwork.Item");
        });
        this.AddNumber(artwork, 10, "Artwork.Format", () => 1);
        artwork.SetMethod<SaveFile>(9, (_, path) =>
        {
            var file = Marshal.PtrToStringBSTR(path);
            this._exportedFiles.Add(file);
            File.WriteAllBytes(file, [1, 2, 3]);
            return this.Call("Artwork.Save");
        });

        var onEvent = typeof(ITunesBackend).GetMethod("OnITunesEvent", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Action<int>>(this.Backend);
        var sink = new ITunesEventSink(onEvent);
        this._sink = sink.IUnknownPointer;
        var addRef = Marshal.GetDelegateForFunctionPointer<ReferenceCount>(ITunesNative.GetMethod(this._sink, 1));
        _ = addRef(this._sink);
        connectionPoint.SetMethod<Unadvise>(6, (instance, cookie) =>
        {
            this.CheckReleaseThread("Unadvise");
            _ = ITunesNative.Release(this._sink);
            this._sink = 0;
            return 0;
        });
        this.SetBackendField("_eventSink", sink);
        this.SetBackendField("_connectionPoint", connectionPoint.Pointer);
        this.SetBackendField("_adviseCookie", 1u);
        this.SetBackendField("_iTunesApp", app.Pointer);
        this.SetBackendField("_isConnected", true);
        this.SetBackendField("_executablePath", @"C:\Program Files\iTunes\iTunes.exe");
        typeof(ITunesBackend).GetMethod("TrackConnectedProcess", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(this.Backend, [Process.GetCurrentProcess()]);
    }

    private NativeObject AddObject(string name, int slots, int references = 0)
    {
        var item = new NativeObject(name, slots, references, this);
        this._objects.Add(item);
        return item;
    }

    private void AddNumber(NativeObject item, int slot, string member, Func<int> getValue) =>
        item.SetMethod<GetNumber>(slot, (nint _, out int value) =>
        {
            value = getValue();
            return this.Call(member);
        });

    private void AddObjectGetter(NativeObject item, int slot, string member, NativeObject target) =>
        item.SetMethod<GetPointer>(slot, (nint _, out nint value) =>
        {
            value = target.Acquire();
            return this.Call(member);
        });

    private int Call(string member)
    {
        if (this._quitSent)
        {
            this._callsAfterQuit.Add(member);
        }

        this._nativeDepth++;
        try
        {
            if (!this._quitSent && this.QuitDuring == member)
            {
                this.SendQuit();
            }
        }
        finally
        {
            this._nativeDepth--;
        }

        return member == "PlayerState" ? this.StateReadResult : 0;
    }

    private void CheckReleaseThread(string name)
    {
        if (Environment.CurrentManagedThreadId != this._ownerThread || this._nativeDepth != 0)
        {
            this._lifetimeErrors.Add($"{name} released outside its dispatcher or during a native call.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await this.Backend.DisposeAsync();
        foreach (var item in this._objects)
        {
            item.Dispose();
        }
    }

    private sealed class NativeObject : IDisposable
    {
        private readonly List<Delegate> _methods = [];
        private readonly nint _vtable;

        public NativeObject(string name, int slots, int references, ITunesNativeFixture owner)
        {
            this.Name = name;
            this.References = references;
            this._vtable = Marshal.AllocHGlobal(slots * nint.Size);
            Marshal.Copy(new nint[slots], 0, this._vtable, slots);
            this.Pointer = Marshal.AllocHGlobal(nint.Size);
            Marshal.WriteIntPtr(this.Pointer, this._vtable);
            this.SetMethod<ReferenceCount>(2, _ =>
            {
                owner.CheckReleaseThread(this.Name);
                this.References--;
                if (this.References < 0)
                {
                    owner._lifetimeErrors.Add($"{this.Name} released more references than it owned.");
                }

                if (this.Name == "App")
                {
                    owner._appReleased.TrySetResult();
                }

                return (uint)this.References;
            });
        }

        public string Name { get; }
        public nint Pointer { get; }
        public int References { get; private set; }

        public nint Acquire()
        {
            this.References++;
            return this.Pointer;
        }

        public void SetMethod<T>(int slot, T method) where T : Delegate
        {
            this._methods.Add(method);
            Marshal.WriteIntPtr(this._vtable, slot * nint.Size, Marshal.GetFunctionPointerForDelegate(method));
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(this.Pointer);
            Marshal.FreeHGlobal(this._vtable);
            GC.KeepAlive(this._methods);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReferenceCount(nint instance);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Command(nint instance);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetNumber(nint instance, out int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetBoolean(nint instance, out short value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPointer(nint instance, out nint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetNumber(nint instance, int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetBoolean(nint instance, short value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetItem(nint instance, int index, out nint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SaveFile(nint instance, nint path);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Unadvise(nint instance, uint cookie);
}