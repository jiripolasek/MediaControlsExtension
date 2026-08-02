// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.ComponentModel;
using System.Diagnostics;

namespace JPSoftworks.MediaControlsExtension;

internal static class DiagnosticEvent
{
    private static readonly TimeSpan SlowSubscriberThreshold = TimeSpan.FromMilliseconds(250);

    public static void Raise(
        object sender,
        EventHandler? handlers,
        string eventName,
        ILogger logger,
        ExtensionOperationDiagnostics? parentDiagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(logger);
        if (handlers is null)
        {
            return;
        }

        var diagnostics = parentDiagnostics ?? new ExtensionOperationDiagnostics(
            $"event {eventName}",
            logger);
        var ownsDiagnostics = parentDiagnostics is null;
        var outcome = "completed";
        try
        {
            diagnostics.SetStage($"capturing subscribers for {eventName}");
            var subscribers = handlers.GetInvocationList();
            foreach (var subscriberDelegate in subscribers)
            {
                var subscriber = (EventHandler)subscriberDelegate;
                var declaringType = subscriber.Method.DeclaringType?.FullName ?? "unknown type";
                var subscriberName = $"{declaringType}.{subscriber.Method.Name}";
                diagnostics.SetStage($"invoking {eventName} subscriber {subscriberName}");
                var startedTimestamp = Stopwatch.GetTimestamp();
                try
                {
                    subscriber(sender, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    outcome = "failed";
                    ExtensionLog.Error(
                        logger,
                        $"Event subscriber {subscriberName} failed while handling {eventName}.",
                        ex);
                    throw;
                }

                var elapsed = Stopwatch.GetElapsedTime(startedTimestamp);
                if (elapsed >= SlowSubscriberThreshold)
                {
                    ExtensionLog.Warning(
                        logger,
                        $"Event subscriber {subscriberName} returned after {elapsed} while handling {eventName}.");
                }
            }

            diagnostics.SetStage($"all subscribers completed for {eventName}");
        }
        finally
        {
            if (ownsDiagnostics)
            {
                diagnostics.Complete(outcome);
            }
        }
    }

    public static void Raise<TEventArgs>(
        object sender,
        EventHandler<TEventArgs>? handlers,
        TEventArgs args,
        string eventName,
        ILogger logger,
        ExtensionOperationDiagnostics? parentDiagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(logger);
        if (handlers is null)
        {
            return;
        }

        var diagnostics = parentDiagnostics ?? new ExtensionOperationDiagnostics(
            $"event {eventName}",
            logger);
        var ownsDiagnostics = parentDiagnostics is null;
        var outcome = "completed";
        try
        {
            diagnostics.SetStage($"capturing subscribers for {eventName}");
            var subscribers = handlers.GetInvocationList();
            foreach (var subscriberDelegate in subscribers)
            {
                var subscriber = (EventHandler<TEventArgs>)subscriberDelegate;
                var declaringType = subscriber.Method.DeclaringType?.FullName ?? "unknown type";
                var subscriberName = $"{declaringType}.{subscriber.Method.Name}";
                diagnostics.SetStage($"invoking {eventName} subscriber {subscriberName}");
                var startedTimestamp = Stopwatch.GetTimestamp();
                try
                {
                    subscriber(sender, args);
                }
                catch (Exception ex)
                {
                    outcome = "failed";
                    ExtensionLog.Error(
                        logger,
                        $"Event subscriber {subscriberName} failed while handling {eventName}.",
                        ex);
                    throw;
                }

                var elapsed = Stopwatch.GetElapsedTime(startedTimestamp);
                if (elapsed >= SlowSubscriberThreshold)
                {
                    ExtensionLog.Warning(
                        logger,
                        $"Event subscriber {subscriberName} returned after {elapsed} while handling {eventName}.");
                }
            }

            diagnostics.SetStage($"all subscribers completed for {eventName}");
        }
        finally
        {
            if (ownsDiagnostics)
            {
                diagnostics.Complete(outcome);
            }
        }
    }

    public static void RaisePropertyChanged(
        object sender,
        PropertyChangedEventHandler? handlers,
        PropertyChangedEventArgs args,
        string eventName,
        ILogger logger,
        ExtensionOperationDiagnostics? parentDiagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(logger);
        if (handlers is null)
        {
            return;
        }

        var diagnostics = parentDiagnostics ?? new ExtensionOperationDiagnostics(
            $"event {eventName}",
            logger);
        var ownsDiagnostics = parentDiagnostics is null;
        var outcome = "completed";
        try
        {
            diagnostics.SetStage($"capturing subscribers for {eventName}");
            var subscribers = handlers.GetInvocationList();
            foreach (var subscriberDelegate in subscribers)
            {
                var subscriber = (PropertyChangedEventHandler)subscriberDelegate;
                var declaringType = subscriber.Method.DeclaringType?.FullName ?? "unknown type";
                var subscriberName = $"{declaringType}.{subscriber.Method.Name}";
                diagnostics.SetStage($"invoking {eventName} subscriber {subscriberName}");
                var startedTimestamp = Stopwatch.GetTimestamp();
                try
                {
                    subscriber(sender, args);
                }
                catch (Exception ex)
                {
                    outcome = "failed";
                    ExtensionLog.Error(
                        logger,
                        $"Event subscriber {subscriberName} failed while handling {eventName}.",
                        ex);
                    throw;
                }

                var elapsed = Stopwatch.GetElapsedTime(startedTimestamp);
                if (elapsed >= SlowSubscriberThreshold)
                {
                    ExtensionLog.Warning(
                        logger,
                        $"Event subscriber {subscriberName} returned after {elapsed} while handling {eventName}.");
                }
            }

            diagnostics.SetStage($"all subscribers completed for {eventName}");
        }
        finally
        {
            if (ownsDiagnostics)
            {
                diagnostics.Complete(outcome);
            }
        }
    }
}
