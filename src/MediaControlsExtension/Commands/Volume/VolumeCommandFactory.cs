// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

namespace JPSoftworks.MediaControlsExtension.Commands;

internal static class VolumeCommandFactory
{
    private static readonly int[] PresetPercentagesValues = [0, 25, 50, 75, 100];

    public static CommandItem[] CreatePresetCommandItems(
        SystemVolumeService systemVolumeService,
        MediaCommandResultFactory resultFactory,
        IIconService iconService,
        IconSurface iconSurface,
        ILoggerFactory loggerFactory)
    {
        var items = new CommandItem[PresetPercentagesValues.Length];
        for (var i = 0; i < PresetPercentagesValues.Length; i++)
        {
            var command = CreatePresetCommand(
                PresetPercentagesValues[i],
                systemVolumeService,
                resultFactory,
                loggerFactory);
            items[i] = new(command)
            {
                Title = command.Name,
                Icon = VolumePresentation.GetThemedIcon(
                    PresetPercentagesValues[i],
                    iconService,
                    iconSurface),
            };
        }

        return items;
    }

    public static CommandContextItem[] CreatePresetContextItems(
        SystemVolumeService systemVolumeService,
        MediaCommandResultFactory resultFactory,
        ILoggerFactory loggerFactory)
    {
        var items = new CommandContextItem[PresetPercentagesValues.Length];
        for (var i = 0; i < PresetPercentagesValues.Length; i++)
        {
            var command = CreatePresetCommand(
                PresetPercentagesValues[i],
                systemVolumeService,
                resultFactory,
                loggerFactory);
            items[i] = new(command)
            {
                Icon = command.Icon,
            };
        }

        return items;
    }

    private static SetVolumeMediaInvokableCommand CreatePresetCommand(
        int volumePercent,
        SystemVolumeService systemVolumeService,
        MediaCommandResultFactory resultFactory,
        ILoggerFactory loggerFactory)
        => new(volumePercent, systemVolumeService, resultFactory, loggerFactory);
}
