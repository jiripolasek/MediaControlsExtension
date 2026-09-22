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

namespace JPSoftworks.MediaControlsExtension.Media.Tests;

[TestClass]
public sealed class ITunesQuitTests
{
    public static IEnumerable<object[]> RefreshQuitCases
    {
        get
        {
            foreach (var eventId in new[] { 8, 9 })
            {
                foreach (var member in new[]
                {
                    "PlayerState", "CurrentPlaylist", "Playlist.Repeat", "CurrentTrack", "Track.Name",
                    "Track.DatabaseId", "Track.Artwork", "Artwork.Item", "Artwork.Format", "Artwork.Save", "PlayerPosition",
                })
                {
                    yield return [eventId, member];
                }
            }
        }
    }

    [TestMethod]
    [DataRow(8, true)]
    [DataRow(9, true)]
    [DataRow(8, false)]
    [DataRow(9, false)]
    public async Task QuitRejectsCommandsQueuedBeforeOrSubmittedAfterNotification(int eventId, bool queueBeforeQuit)
    {
        await using var fixture = await NativeFixture.CreateAsync(eventId);
        var commands = new List<Task<MediaBackendCommandResult>>();
        await fixture.RunAsync(() =>
        {
            if (!queueBeforeQuit)
            {
                fixture.SendQuit();
            }

            foreach (var operation in new[]
            {
                MediaOperation.Play, MediaOperation.Pause, MediaOperation.Stop, MediaOperation.SkipNext,
                MediaOperation.SkipPrevious, MediaOperation.ToggleShuffle, MediaOperation.ToggleRepeat,
            })
            {
                commands.Add(fixture.ExecuteAsync(operation));
            }

            if (queueBeforeQuit)
            {
                fixture.SendQuit();
            }
        });

        foreach (var result in await Task.WhenAll(commands))
        {
            Assert.AreEqual(MediaBackendCommandStatus.Unavailable, result.Status);
        }

        await fixture.AssertQuitCompletedAsync();
    }

    [TestMethod]
    [DataRow(8)]
    [DataRow(9)]
    public async Task QuitDropsAnAlreadyQueuedRefresh(int eventId)
    {
        await using var fixture = await NativeFixture.CreateAsync(eventId);
        await fixture.RunAsync(() =>
        {
            fixture.RequestRefresh();
            fixture.SendQuit();
        });

        await fixture.AssertQuitCompletedAsync();
    }

    [TestMethod]
    [DynamicData(nameof(RefreshQuitCases))]
    public async Task QuitDuringRefreshUnwindsOwnedObjectsWithoutFurtherCalls(int eventId, string member)
    {
        await using var fixture = await NativeFixture.CreateAsync(eventId);
        fixture.QuitDuring = member;
        fixture.RequestRefresh();

        await fixture.AssertQuitCompletedAsync();
    }

    [TestMethod]
    [DataRow(8, MediaOperation.Play, "Play")]
    [DataRow(9, MediaOperation.Play, "Play")]
    [DataRow(8, MediaOperation.Play, "PlayerState")]
    [DataRow(9, MediaOperation.Play, "PlayerState")]
    [DataRow(8, MediaOperation.Play, "PlayerPosition")]
    [DataRow(9, MediaOperation.Play, "PlayerPosition")]
    [DataRow(8, MediaOperation.ToggleShuffle, "CurrentPlaylist")]
    [DataRow(9, MediaOperation.ToggleShuffle, "CurrentPlaylist")]
    [DataRow(8, MediaOperation.ToggleShuffle, "Playlist.Shuffle")]
    [DataRow(9, MediaOperation.ToggleShuffle, "Playlist.Shuffle")]
    [DataRow(8, MediaOperation.ToggleShuffle, "Playlist.SetShuffle")]
    [DataRow(9, MediaOperation.ToggleShuffle, "Playlist.SetShuffle")]
    [DataRow(8, MediaOperation.ToggleRepeat, "Playlist.Repeat")]
    [DataRow(9, MediaOperation.ToggleRepeat, "Playlist.Repeat")]
    [DataRow(8, MediaOperation.ToggleRepeat, "Playlist.SetRepeat")]
    [DataRow(9, MediaOperation.ToggleRepeat, "Playlist.SetRepeat")]
    public async Task QuitDuringCommandStopsRemainingCallsAndReturnsUnavailable(int eventId, MediaOperation operation, string member)
    {
        await using var fixture = await NativeFixture.CreateAsync(eventId);
        fixture.QuitDuring = member;

        var result = await fixture.ExecuteAsync(operation);

        Assert.AreEqual(MediaBackendCommandStatus.Unavailable, result.Status);
        await fixture.AssertQuitCompletedAsync();
    }

    [TestMethod]
    [DataRow(8)]
    [DataRow(9)]
    public async Task QuitDuringFailedReadLeavesCleanupToTheQuitHandler(int eventId)
    {
        await using var fixture = await NativeFixture.CreateAsync(eventId);
        fixture.QuitDuring = "PlayerState";
        fixture.StateReadResult = unchecked((int)0x80010108);
        fixture.RequestRefresh();

        await fixture.AssertQuitCompletedAsync();
        Assert.AreEqual(Environment.ProcessId, fixture.GetBackendField("_quittingProcessId"));
    }

    [TestMethod]
    [DataRow(8, 0)]
    [DataRow(9, 0)]
    [DataRow(8, unchecked((int)0x80010108))]
    [DataRow(9, unchecked((int)0x80010108))]
    public async Task QuitDuringMissingTrackReadLeavesSessionRemovalToTheQuitHandler(int eventId, int result)
    {
        await using var fixture = await NativeFixture.CreateAsync(eventId);
        Assert.AreEqual(MediaBackendCommandStatus.Completed, (await fixture.ExecuteAsync(MediaOperation.Play)).Status);
        var before = await fixture.Backend.ReadSnapshotAsync(default);
        Assert.HasCount(1, before.Sessions);
        fixture.QuitDuring = "CurrentTrack";
        fixture.HasCurrentTrack = false;
        fixture.TrackReadResult = result;
        fixture.RequestRefresh();

        await fixture.AssertQuitCompletedAsync();
        var after = await fixture.Backend.ReadSnapshotAsync(default);
        Assert.AreEqual(before.Revision + 1, after.Revision, "Quit should publish only the final disconnected state.");
    }

    [TestMethod]
    public async Task NormalCommandsAndRefreshStillPublishMediaAndReleaseTemporaryObjects()
    {
        await using var fixture = await NativeFixture.CreateAsync(9);
        foreach (var operation in new[] { MediaOperation.Play, MediaOperation.ToggleShuffle, MediaOperation.ToggleRepeat })
        {
            var result = await fixture.ExecuteAsync(operation);
            Assert.AreEqual(MediaBackendCommandStatus.Completed, result.Status);
        }

        var snapshot = await fixture.Backend.ReadSnapshotAsync(default);
        Assert.AreEqual(MediaBackendConnectionState.Connected, snapshot.Connection);
        Assert.AreEqual("Track", snapshot.Sessions.Single().MediaProperties.Title);
        Assert.IsNotNull(snapshot.Sessions.Single().MediaProperties.Artwork);
        Assert.IsTrue(fixture.ShuffleEnabled);
        Assert.AreEqual((int)ITPlaylistRepeatMode.All, fixture.RepeatMode);
        fixture.AssertTemporaryObjectsReleased();
    }

    private sealed class NativeFixture : IAsyncDisposable
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

        private NativeFixture(int eventId) => this._eventId = eventId;

        public ITunesBackend Backend { get; } = new(isProcessRunning: static _ => false);
        public string? QuitDuring { get; set; }
        public int StateReadResult { get; set; }
        public int TrackReadResult { get; set; }
        public bool HasCurrentTrack { get; set; } = true;
        public bool ShuffleEnabled { get; private set; }
        public int RepeatMode { get; private set; }

        public static async Task<NativeFixture> CreateAsync(int eventId)
        {
            var fixture = new NativeFixture(eventId);
            await fixture.Backend.StartAsync(default);
            fixture._queue = (DispatcherQueue)fixture.GetBackendField("_dispatcherQueue")!;
            await fixture.RunAsync(fixture.Connect);
            return fixture;
        }

        public object? GetBackendField(string name) =>
            typeof(ITunesBackend).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this.Backend);

        private void SetBackendField(string name, object value) =>
            typeof(ITunesBackend).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this.Backend, value);

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
            _ = ITunesNative.DispatchInvoke(this._sink, this._eventId);
        }

        public async Task AssertQuitCompletedAsync()
        {
            await this._appReleased.Task.WaitAsync(Timeout);
            await this.RunAsync(static () => { });
            Assert.IsEmpty(this._callsAfterQuit, string.Join(", ", this._callsAfterQuit));
            Assert.IsEmpty(this._lifetimeErrors, string.Join(", ", this._lifetimeErrors));
            Assert.IsTrue(this._objects.All(static item => item.References == 0));
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
                app.SetMethod<Command>(slot, _ => this.Call(name));
            }

            this.AddNumber(app, 39, "PlayerState", () => 1);
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
                return 0;
            });
            this.SetBackendField("_eventSink", sink);
            this.SetBackendField("_connectionPoint", connectionPoint.Pointer);
            this.SetBackendField("_adviseCookie", 1u);
            this.SetBackendField("_iTunesApp", app.Pointer);
            this.SetBackendField("_isConnected", true);
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

            public NativeObject(string name, int slots, int references, NativeFixture owner)
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
}