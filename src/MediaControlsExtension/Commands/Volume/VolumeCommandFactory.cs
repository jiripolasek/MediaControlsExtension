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
        YetAnotherHelper yetAnotherHelper)
    {
        var items = new CommandItem[PresetPercentagesValues.Length];
        for (var i = 0; i < PresetPercentagesValues.Length; i++)
        {
            var command = CreatePresetCommand(PresetPercentagesValues[i], systemVolumeService, yetAnotherHelper);
            items[i] = new(command)
            {
                Title = command.Name,
                Icon = command.Icon,
            };
        }

        return items;
    }

    public static CommandContextItem[] CreatePresetContextItems(
        SystemVolumeService systemVolumeService,
        YetAnotherHelper yetAnotherHelper)
    {
        var items = new CommandContextItem[PresetPercentagesValues.Length];
        for (var i = 0; i < PresetPercentagesValues.Length; i++)
        {
            var command = CreatePresetCommand(PresetPercentagesValues[i], systemVolumeService, yetAnotherHelper);
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
        YetAnotherHelper yetAnotherHelper)
        => new(volumePercent, systemVolumeService, yetAnotherHelper);
}
