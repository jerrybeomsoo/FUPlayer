using System.Text.Json.Nodes;

namespace FUPlayer.Core.Settings;

/// <summary>
/// Upgrades settings files written by earlier 0.1 development builds, which spelled some properties differently
/// and stored defaults that have since changed. The old names below exist only so that those files keep working;
/// a property that is no longer recognised falls back to its default when the file is deserialised.
/// </summary>
internal static class SettingsMigration
{
    public static void Upgrade(JsonObject root)
    {
        int version = root["Version"] is JsonValue value && value.TryGetValue(out int parsed) ? parsed : 1;
        if (version >= PlayerSettings.CurrentVersion)
        {
            return;
        }

        Rename(root, "DsdSource", "DsdToPcm");
        Rename(root, "Balance", "Speakers");

        if (root["Output"] is JsonObject output)
        {
            if (output["Mode"] is JsonValue mode && mode.TryGetValue(out string? text) && text == "Source")
            {
                output["Mode"] = "FollowSource";
            }

            Rename(output, "ChannelOffset", "FirstChannel");
            Rename(output, "Allow48kDsd", "Use48kDsdRates");
            Rename(output, "ShortBuffer", "LowLatencyFifo");
            Rename(output, "QuickPause", "InstantPause");
        }

        if (root["Dsd"] is JsonObject dsd)
        {
            Rename(dsd, "DsdMultiplierLimit", "HighestMultiplier");
            Rename(dsd, "RemodulationFilterId", "InputFilterId");
        }

        if (root["DsdToPcm"] is JsonObject toPcm)
        {
            Rename(toPcm, "Gain6Db", "RestoreLevel");
        }

        if (root["Volume"] is JsonObject volume)
        {
            Rename(volume, "PcmGainCompensationDb", "PcmLevelOffsetDb");
            volume.Remove("IsBypassed");
        }

        if (root["Processing"] is JsonObject processing)
        {
            Rename(processing, "UltrasonicFilter", "RemoveUltrasonics");
            Rename(processing, "PreProcessBeforeMeter", "MeterAdjustedSource");
        }

        if (version < 3)
        {
            // Three defaults changed rather than three settings: one block length for every tap, because it is
            // the only layout a graphics device takes, and single-stage filtering for both output modes. A file
            // written before the change stores the old default explicitly, so the stored value is dropped and
            // the new default applies. A deliberate choice of the old value is lost with it, which is the price
            // of the change reaching anyone who had already run the player.
            (root["Processing"] as JsonObject)?.Remove("ConvolutionUniformBlocks");
            (root["Pcm"] as JsonObject)?.Remove("Staging");
            (root["Dsd"] as JsonObject)?.Remove("Staging");
        }

        if (version < 4 && root["Restoration"] is JsonObject restoration)
        {
            // Lossy repair became the neural upscaler alone. The master switch it had is gone, so a file
            // that had it off keeps the upscaler off; the difference monitor is now the output delta; the
            // older stages and their models no longer exist, and their settings are dropped.
            if (restoration["Enabled"] is JsonValue enabled && enabled.TryGetValue(out bool on) && !on)
            {
                restoration["NeuralUpscaler"] = false;
            }

            Rename(restoration, "OutputDifference", "OutputDelta");
            foreach (string gone in new[]
            {
                "Enabled", "ReduceArtifacts", "ArtifactStrength", "RebuildHarmonics", "RebuildAmountDb", "Predict",
                "ModelPath", "NetworkPath", "NetworkAmount", "ManualCutoffHz", "CeilingHz", "RebuildUltrasonics",
                "UltrasonicPath", "UltrasonicTrimDb",
            })
            {
                restoration.Remove(gone);
            }
        }

        root["Version"] = PlayerSettings.CurrentVersion;
    }

    private static void Rename(JsonObject section, string from, string to)
    {
        if (!section.TryGetPropertyValue(from, out JsonNode? value))
        {
            return;
        }

        section.Remove(from);
        if (!section.ContainsKey(to))
        {
            section[to] = value;
        }
    }
}
