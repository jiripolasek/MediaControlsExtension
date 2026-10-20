// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Microsoft.Extensions.Logging;

namespace JPSoftworks.MediaControlsExtension.Pages;

internal sealed partial class MediaBackendConfigurationPage : ContentPage
{
    private static readonly Action<ILogger, Exception?> SaveFailed = LoggerMessage.Define(
        LogLevel.Warning, new EventId(1, nameof(SaveFailed)), "Could not save media source configuration.");
    private readonly Lock _gate = new();
    private readonly Settings _settings;
    private readonly Action _save;
    private readonly ILogger _logger;
    public Func<string, string?>? ValidateInputs { get; init; }
    public Func<string?>? GetSaveError { get; init; }
    public Action? EnableSource { get; init; }
    public Func<bool>? IsSourceEnabled { get; init; }

    public MediaBackendConfigurationPage(string id, string title, Settings settings, Action save, ILoggerFactory loggerFactory)
    {
        this.Id = $"com.jpsoftworks.cmdpal.mediacontrols.sources.{id}.configuration";
        this.Name = MediaSourcesPage.Text("Configure");
        this.Title = title;
        this.Icon = new IconInfo("\uE713");
        this._settings = settings;
        this._save = save;
        this._logger = loggerFactory.CreateLogger<MediaBackendConfigurationPage>();
    }

    public override IContent[] GetContent()
    {
        lock (this._gate)
        {
            var template = ((IFormContent)this._settings.ToContent()[0]).TemplateJson;
            if (this.EnableSource is not null && this.IsSourceEnabled?.Invoke() != true)
            {
                var card = JsonNode.Parse(template)!.AsObject();
                var actions = card["actions"] as JsonArray ?? new JsonArray();
                actions.Insert(0, new JsonObject
                {
                    ["type"] = "Action.Submit",
                    ["title"] = MediaSourcesPage.Text("SaveAndEnable"),
                    ["data"] = new JsonObject { ["enableSource"] = true },
                });
                card["actions"] = actions;
                template = card.ToJsonString();
            }

            return [new ConfigurationForm(this, template)];
        }
    }

    private CommandResult Save(string inputs, string data)
    {
        var saved = false;
        try
        {
            var enable = !string.IsNullOrWhiteSpace(data) &&
                JsonNode.Parse(data)?["enableSource"]?.GetValue<bool>() == true;
            lock (this._gate)
            {
                if (this.ValidateInputs?.Invoke(inputs) is { } error)
                {
                    return CommandResult.ShowToast(new ToastArgs { Message = error, Result = CommandResult.KeepOpen() });
                }

                this._settings.Update(inputs);
                this._save();
                saved = true;
                if (this.GetSaveError?.Invoke() is { } saveError)
                {
                    return CommandResult.ShowToast(new ToastArgs { Message = saveError, Result = CommandResult.KeepOpen() });
                }
            }

            if (enable)
            {
                this.EnableSource?.Invoke();
            }

            return CommandResult.ShowToast(new ToastArgs
            {
                Message = MediaSourcesPage.Text(enable && this.EnableSource is not null ? "SavedAndEnabled" : "Saved"),
                Result = CommandResult.KeepOpen(),
            });
        }
        catch (Exception ex)
        {
            SaveFailed(this._logger, ex);
            return CommandResult.ShowToast(new ToastArgs
            {
                Message = MediaSourcesPage.Text(saved ? "SavedButEnableFailed" : "SaveFailed"),
                Result = CommandResult.KeepOpen(),
            });
        }
        finally
        {
            if (saved)
            {
                this.RaiseItemsChanged();
            }
        }
    }

    private sealed partial class ConfigurationForm : FormContent
    {
        private readonly MediaBackendConfigurationPage _owner;

        public ConfigurationForm(MediaBackendConfigurationPage owner, string template)
        {
            this._owner = owner;
            this.TemplateJson = template;
        }

        public override ICommandResult SubmitForm(string inputs, string data) => this._owner.Save(inputs, data);
    }
}