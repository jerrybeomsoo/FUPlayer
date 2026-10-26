using System.Diagnostics;

namespace FUPlayer.Core.Streaming;

/// <summary>Starts an installed Firefox with a private-to-the-player profile, without changing its sandbox or DRM.</summary>
public static class FirefoxLaunch
{
    // An XPI navigation opens Firefox's installer rather than becoming a document. The setup tab remains blank.
    public const string PersistentSetupAddress = "about:blank";

    /// <summary>Keeps an installed add-on from closing Firefox's update confirmation before its replacement connects.</summary>
    public static string PersistentSessionSetupAddress(bool installingAddon, bool reopenAfterInstall) =>
        // This unmatched address is only native-session data, never a browser navigation. After the new version
        // acknowledges, the player reopens Firefox normally so the service popup can consume its blank setup tab.
        installingAddon && reopenAfterInstall ? "about:blank#fuplayer-addon-upgrade" : PersistentSetupAddress;

    /// <summary>Matches an owned window's marker without depending on Firefox's origin label or localised title format.</summary>
    public static bool WindowTitleMatches(string title, string marker) =>
        marker.Length > 0 && title.Contains(marker, StringComparison.Ordinal);

    /// <summary>The dedicated profile; it never points at a user's normal Firefox profile.</summary>
    public static string ProfileDirectory(string settingsDirectory) =>
        Path.Combine(Path.GetFullPath(settingsDirectory), "browser", "firefox");

    /// <summary>Only ordinary web addresses can be supplied by the service picker.</summary>
    public static Uri Address(string address)
    {
        address = address.Trim();
        if (address.Length is 0 or > 4096 || address.Any(char.IsControl))
            throw new ArgumentException("Enter a web address.");
        if (!address.Contains("://", StringComparison.Ordinal)) address = "https://" + address;
        if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0)
            throw new ArgumentException("Use an http or https web address without embedded credentials.");
        return uri;
    }

    /// <summary>Arguments remain separate, including profile paths containing spaces and a URL's query string.</summary>
    public static ProcessStartInfo StartInfo(string executable, string settingsDirectory, string address, bool audioBridgeSetup = false,
        bool persistentAddon = false, string? installAddon = null)
    {
        var start = new ProcessStartInfo(Path.GetFullPath(executable))
        {
            UseShellExecute = false,
            // A native visibility guard keeps routine setup running offscreen; confirmations stay accessible.
            WindowStyle = persistentAddon && installAddon is null ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
        };
        start.ArgumentList.Add("--no-remote");
        // Firefox's Windows launcher otherwise exits before the browser it created. Waiting keeps the owned tree identifiable.
        start.ArgumentList.Add("--wait-for-browser");
        start.ArgumentList.Add("--profile");
        start.ArgumentList.Add(ProfileDirectory(settingsDirectory));
        // Only setup prompts need kiosk. Routine startup hands over to the add-on's chromeless popup;
        // its disposable setup window is parked without minimising Firefox's process at launch.
        start.ArgumentList.Add(persistentAddon && installAddon is null ? "--new-window" : "--kiosk");
        Uri service = Address(address);
        if (installAddon is not null && (!persistentAddon || !Path.GetExtension(installAddon).Equals(".xpi", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Choose a Firefox add-on package.");
        start.ArgumentList.Add(persistentAddon ? installAddon is null ? PersistentSetupAddress : new Uri(Path.GetFullPath(installAddon)).AbsoluteUri
            : audioBridgeSetup ? "about:debugging#/runtime/this-firefox" : service.AbsoluteUri);
        return start;
    }
}
