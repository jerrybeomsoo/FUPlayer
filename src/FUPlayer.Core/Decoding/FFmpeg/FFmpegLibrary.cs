using System.Reflection;
using System.Runtime.InteropServices;

namespace FUPlayer.Core.Decoding.FFmpeg;

/// <summary>Locates and validates the optional FFmpeg 9.0 shared libraries.</summary>
public static class FFmpegLibrary
{
    public const int AvformatMajor = 63;
    public const int AvcodecMajor = 63;
    public const int AvutilMajor = 61;

    private static readonly object Gate = new();
    private static bool? _available;
    private static bool _resolverInstalled;

    /// <summary>Additional directory searched before the default locations (set before first use).</summary>
    public static string? SearchDirectory { get; set; }

    /// <summary>Why FFmpeg is unavailable, when it is.</summary>
    public static string? LoadError { get; private set; }

    /// <summary>Loaded library versions, when available.</summary>
    public static string? VersionDescription { get; private set; }

    public static bool IsAvailable
    {
        get
        {
            lock (Gate)
            {
                _available ??= TryInitialize();
                return _available.Value;
            }
        }
    }

    private static bool TryInitialize()
    {
        try
        {
            if (!_resolverInstalled)
            {
                NativeLibrary.SetDllImportResolver(typeof(FFmpegLibrary).Assembly, Resolve);
                _resolverInstalled = true;
            }

            // Load in dependency order so each library finds the previous one already in the process.
            uint util = FFmpegNative.avutil_version();
            uint codec = FFmpegNative.avcodec_version();
            uint format = FFmpegNative.avformat_version();
            if (util >> 16 != AvutilMajor || codec >> 16 != AvcodecMajor || format >> 16 != AvformatMajor)
            {
                LoadError = $"Unsupported FFmpeg version (avformat {format >> 16}, avcodec {codec >> 16}, avutil {util >> 16}); " +
                    $"FUPlayer requires FFmpeg 9.0 (avformat {AvformatMajor}, avcodec {AvcodecMajor}, avutil {AvutilMajor}).";
                return false;
            }

            FFmpegNative.av_log_set_level(FFmpegNative.AvLogQuiet);
            VersionDescription = $"FFmpeg (avformat {Describe(format)}, avcodec {Describe(codec)}, avutil {Describe(util)})";
            LoadError = null;
            return true;
        }
        catch (DllNotFoundException)
        {
            LoadError = "FFmpeg libraries were not found. See docs/Building-FFmpeg.md.";
        }
        catch (EntryPointNotFoundException ex)
        {
            LoadError = "Incompatible FFmpeg libraries: " + ex.Message;
        }
        catch (BadImageFormatException ex)
        {
            LoadError = "FFmpeg libraries have the wrong architecture: " + ex.Message;
        }
        catch (InvalidOperationException ex)
        {
            LoadError = ex.Message;
        }

        return false;
    }

    private static string Describe(uint version) => $"{version >> 16}.{(version >> 8) & 0xFF}.{version & 0xFF}";

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        int major = libraryName switch
        {
            FFmpegNative.AvformatLibrary => AvformatMajor,
            FFmpegNative.AvcodecLibrary => AvcodecMajor,
            FFmpegNative.AvutilLibrary => AvutilMajor,
            _ => -1,
        };

        if (major < 0)
        {
            return IntPtr.Zero;
        }

        foreach (string file in CandidateFileNames(libraryName, major))
        {
            foreach (string directory in SearchDirectories())
            {
                string path = Path.Combine(directory, file);
                if (File.Exists(path) && NativeLibrary.TryLoad(path, out IntPtr handle))
                {
                    return handle;
                }
            }

            if (NativeLibrary.TryLoad(file, assembly, searchPath, out IntPtr systemHandle))
            {
                return systemHandle;
            }
        }

        return IntPtr.Zero;
    }

    private static IEnumerable<string> CandidateFileNames(string name, int major)
    {
        if (OperatingSystem.IsWindows())
        {
            yield return $"{name}-{major}.dll";
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return $"lib{name}.{major}.dylib";
        }
        else
        {
            yield return $"lib{name}.so.{major}";
        }
    }

    private static IEnumerable<string> SearchDirectories()
    {
        if (!string.IsNullOrWhiteSpace(SearchDirectory))
        {
            yield return SearchDirectory;
        }

        string? fromEnvironment = Environment.GetEnvironmentVariable("FUPLAYER_FFMPEG_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            yield return fromEnvironment;
        }

        string baseDirectory = AppContext.BaseDirectory;
        yield return baseDirectory;
        yield return Path.Combine(baseDirectory, "ffmpeg");
        yield return Path.Combine(baseDirectory, "native", RuntimeInformation.RuntimeIdentifier);
    }
}
