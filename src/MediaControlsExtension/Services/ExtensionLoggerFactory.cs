// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using Microsoft.Extensions.Logging;
using ToolkitLogger = JPSoftworks.CommandPalette.Extensions.Toolkit.Logging.Logger;

namespace JPSoftworks.MediaControlsExtension.Services;

internal sealed partial class ExtensionLoggerFactory : ILoggerFactory
{
    public static ExtensionLoggerFactory Instance { get; } = new();

    private ExtensionLoggerFactory()
    {
    }

    public ILogger CreateLogger(string categoryName) => new ExtensionLogger(categoryName);

    public void AddProvider(ILoggerProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
    }

    public void Dispose()
    {
    }

    private sealed partial class ExtensionLogger(string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (!this.IsEnabled(logLevel))
            {
                return;
            }

            var message = $"[{categoryName}] {formatter(state, exception)}";
            switch (logLevel)
            {
                case LogLevel.Critical:
                case LogLevel.Error:
                    if (exception is null)
                    {
                        ToolkitLogger.LogError(new InvalidOperationException(message));
                    }
                    else
                    {
                        ToolkitLogger.LogError(message, exception);
                    }

                    break;
                case LogLevel.Warning:
                    ToolkitLogger.LogWarning(exception is null
                        ? message
                        : $"{message}{Environment.NewLine}{exception}");
                    break;
                default:
                    ToolkitLogger.LogInformation(exception is null
                        ? message
                        : $"{message}{Environment.NewLine}{exception}");
                    break;
            }
        }
    }
}