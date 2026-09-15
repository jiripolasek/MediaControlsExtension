namespace JPSoftworks.MediaControlsExtension.Media.Hosting;

internal sealed class WorkerHealth(TimeProvider? timeProvider = null)
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private long? _healthySince;
    private bool _completed;
    private bool _wasStable;

    public bool WasStable { get { lock (this._gate) { return this._wasStable; } } }

    public void Observe(bool healthy)
    {
        lock (this._gate)
        {
            if (this._completed) { return; }
            this.CheckStablePeriod();
            this._healthySince = healthy ? this._healthySince ?? this._clock.GetTimestamp() : null;
        }
    }

    public void Complete()
    {
        lock (this._gate)
        {
            if (this._completed) { return; }
            this.CheckStablePeriod();
            this._completed = true;
        }
    }

    private void CheckStablePeriod()
    {
        if (this._healthySince is { } since && this._clock.GetElapsedTime(since) >= TimeSpan.FromSeconds(30))
        {
            this._wasStable = true;
        }
    }
}