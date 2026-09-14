using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FUPlayer.Core.Dsp.Analysis;
using FUPlayer.Core.Dsp.Dsd;
using FUPlayer.Core.Dsp.Modulation;
using FUPlayer.Core.Dsp.Quantization;
using FUPlayer.Core.Dsp.Resampling;

namespace FUPlayer.Core.Settings;

/// <summary>Loads and saves <see cref="PlayerSettings"/> as JSON in the user's application-data folder.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public SettingsStore(string? directory = null)
    {
        SettingsDirectory = directory ?? DefaultDirectory;
    }

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FUPlayer");

    public string SettingsDirectory { get; }

    public string FilePath => Path.Combine(SettingsDirectory, "settings.json");

    public PlayerSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return Parse(File.ReadAllText(FilePath));
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            // Corrupt or unreadable settings fall back to defaults.
        }

        return new PlayerSettings();
    }

    public void Save(PlayerSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        string temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
        File.Move(temporary, FilePath, overwrite: true);
    }

    public static PlayerSettings Clone(PlayerSettings settings) =>
        Normalize(JsonSerializer.Deserialize<PlayerSettings>(JsonSerializer.Serialize(settings, Options), Options)!);

    /// <summary>Reads settings JSON, upgrading files written by earlier versions.</summary>
    internal static PlayerSettings Parse(string json)
    {
        if (JsonNode.Parse(json, documentOptions: DocumentOptions) is not JsonObject root)
        {
            return new PlayerSettings();
        }

        SettingsMigration.Upgrade(root);
        return root.Deserialize<PlayerSettings>(Options) is { } settings ? Normalize(settings) : new PlayerSettings();
    }

    internal static PlayerSettings Normalize(PlayerSettings settings)
    {
        settings.Output ??= new OutputSettings();
        settings.Pcm ??= new PcmSettings();
        settings.Dsd ??= new DsdOutputSettings();
        settings.DsdToPcm ??= new DsdToPcmSettings();
        settings.Volume ??= new VolumeSettings();
        settings.Processing ??= new ProcessingSettings();
        settings.Speakers ??= new SpeakerSettings();
        settings.Playback ??= new PlaybackSettings();
        settings.Library ??= new LibrarySettings();
        settings.Analyzer ??= new AnalyzerSettings();
        settings.Ui ??= new UiSettings();
        settings.Version = PlayerSettings.CurrentVersion;

        settings.Speakers.Channels ??= [];
        for (int i = settings.Speakers.Channels.Count; i < SpeakerSettings.ChannelNames.Length; i++)
        {
            settings.Speakers.Channels.Add(new SpeakerTrim { Name = SpeakerSettings.ChannelNames[i] });
        }

        settings.Library.Folders ??= [];
        settings.Output.Channels = Math.Clamp(settings.Output.Channels, 1, 32);
        settings.Output.FirstChannel = Math.Max(0, settings.Output.FirstChannel);
        settings.Output.BufferMilliseconds = Math.Clamp(settings.Output.BufferMilliseconds, 0, 1000);

        settings.Pcm.Filter = FilterCatalog.TryGet(settings.Pcm.Filter, out _) ? settings.Pcm.Filter : FilterCatalog.DefaultId;
        settings.Pcm.DitherId = DitherCatalog.All.Any(p => p.Id == settings.Pcm.DitherId) ? settings.Pcm.DitherId : DitherCatalog.AutoId;
        settings.Pcm.DacBits = Math.Clamp(settings.Pcm.DacBits, 16, 32);
        settings.Pcm.FilterTaps = NormalizeTaps(settings.Pcm.FilterTaps);

        settings.Dsd.Filter = FilterCatalog.TryGet(settings.Dsd.Filter, out _) ? settings.Dsd.Filter : FilterCatalog.DefaultId;
        settings.Dsd.ModulatorId = ModulatorCatalog.All.Any(p => p.Id == settings.Dsd.ModulatorId) ? settings.Dsd.ModulatorId : ModulatorCatalog.DefaultId;
        settings.Dsd.InputFilterId = DsdFilterCatalog.All.Any(p => p.Id == settings.Dsd.InputFilterId) ? settings.Dsd.InputFilterId : DsdFilterCatalog.WidebandId;
        settings.Dsd.HighestMultiplier = Math.Clamp(settings.Dsd.HighestMultiplier, 64, 1024);
        settings.Dsd.FilterTaps = NormalizeTaps(settings.Dsd.FilterTaps);
        settings.DsdToPcm.FilterId = DsdFilterCatalog.All.Any(p => p.Id == settings.DsdToPcm.FilterId) ? settings.DsdToPcm.FilterId : DsdFilterCatalog.DefaultId;

        settings.Processing.FifoMilliseconds = Math.Clamp(settings.Processing.FifoMilliseconds, 50, 60_000);
        settings.Volume.MinimumDb = Math.Clamp(settings.Volume.MinimumDb, -120.0, 0.0);
        settings.Volume.MaximumDb = Math.Clamp(settings.Volume.MaximumDb, settings.Volume.MinimumDb, 12.0);
        settings.Volume.VolumeDb = Math.Clamp(settings.Volume.VolumeDb, settings.Volume.MinimumDb, settings.Volume.MaximumDb);
        settings.Volume.PcmLevelOffsetDb = Math.Clamp(settings.Volume.PcmLevelOffsetDb, -12.0, 12.0);

        AnalyzerSettings analyzer = settings.Analyzer;
        analyzer.FftSize = SpectrumAnalyzer.NormalizeFftSize(analyzer.FftSize);
        analyzer.FloorDb = Math.Clamp(analyzer.FloorDb, -240.0, -40.0);
        analyzer.ChannelIndex = Math.Clamp(analyzer.ChannelIndex, 0, 63);
        analyzer.BandHz = Math.Max(0.0, analyzer.BandHz);
        return settings;
    }

    /// <summary>0 (automatic) or a sane explicit filter length.</summary>
    private static int NormalizeTaps(int taps) =>
        taps <= 0 ? 0 : Math.Clamp(taps, 1024, ResamplerFactory.MaxPrototypeLength);
}
