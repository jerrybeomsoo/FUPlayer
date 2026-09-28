using System.Security;
using System.Text;

namespace FUPlayer.Core.Upnp;

/// <summary>
/// The documents a control point reads before it talks to the renderer: the device description, which names the
/// device and lists its services, and each service's description (SCPD), which lists its actions and variables.
/// </summary>
internal static class UpnpDescriptions
{
    public const string DeviceType = "urn:schemas-upnp-org:device:MediaRenderer:1";
    public const string AvTransport = "urn:schemas-upnp-org:service:AVTransport:1";
    public const string RenderingControl = "urn:schemas-upnp-org:service:RenderingControl:1";
    public const string ConnectionManager = "urn:schemas-upnp-org:service:ConnectionManager:1";

    /// <summary>The manufacturer and model the description gives, which controllers keep their quirks lists by.</summary>
    public const string Manufacturer = "FUPlayer contributors";

    /// <inheritdoc cref="Manufacturer"/>
    public const string ModelName = "FUPlayer";

    /// <summary>The services, with the short name that makes up their SCPD, control and event paths.</summary>
    public static readonly (string Type, string Id, string Name)[] Services =
    [
        (AvTransport, "urn:upnp-org:serviceId:AVTransport", "AVTransport"),
        (RenderingControl, "urn:upnp-org:serviceId:RenderingControl", "RenderingControl"),
        (ConnectionManager, "urn:upnp-org:serviceId:ConnectionManager", "ConnectionManager"),
    ];

    public static string Device(string udn, string friendlyName, string version)
    {
        var xml = new StringBuilder();
        xml.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        xml.Append("<root xmlns=\"urn:schemas-upnp-org:device-1-0\" xmlns:dlna=\"urn:schemas-dlna-org:device-1-0\">\n");
        xml.Append("  <specVersion><major>1</major><minor>0</minor></specVersion>\n");
        xml.Append("  <device>\n");
        xml.Append("    <deviceType>").Append(DeviceType).Append("</deviceType>\n");
        xml.Append("    <friendlyName>").Append(Escape(friendlyName)).Append("</friendlyName>\n");
        xml.Append("    <manufacturer>").Append(Manufacturer).Append("</manufacturer>\n");
        xml.Append("    <manufacturerURL>https://github.com/jerrybeomsoo/FUPlayer</manufacturerURL>\n");
        xml.Append("    <modelDescription>Free Upscaling Player, receiving over UPnP</modelDescription>\n");
        xml.Append("    <modelName>").Append(ModelName).Append("</modelName>\n");
        xml.Append("    <modelNumber>").Append(Escape(version)).Append("</modelNumber>\n");
        xml.Append("    <modelURL>https://github.com/jerrybeomsoo/FUPlayer</modelURL>\n");
        xml.Append("    <UDN>").Append(udn).Append("</UDN>\n");
        xml.Append("    <dlna:X_DLNADOC>DMR-1.50</dlna:X_DLNADOC>\n");
        xml.Append("    <serviceList>\n");
        foreach ((string type, string id, string name) in Services)
        {
            xml.Append("      <service>\n");
            xml.Append("        <serviceType>").Append(type).Append("</serviceType>\n");
            xml.Append("        <serviceId>").Append(id).Append("</serviceId>\n");
            xml.Append("        <SCPDURL>/scpd/").Append(name).Append(".xml</SCPDURL>\n");
            xml.Append("        <controlURL>/control/").Append(name).Append("</controlURL>\n");
            xml.Append("        <eventSubURL>/event/").Append(name).Append("</eventSubURL>\n");
            xml.Append("      </service>\n");
        }

        xml.Append("    </serviceList>\n");
        xml.Append("  </device>\n");
        xml.Append("</root>\n");
        return xml.ToString();
    }

    /// <summary>A service description, or null for a name that is not one of the services.</summary>
    public static string? Scpd(string name) => name switch
    {
        "AVTransport" => Build(AvTransportActions, AvTransportVariables),
        "RenderingControl" => Build(RenderingControlActions, RenderingControlVariables),
        "ConnectionManager" => Build(ConnectionManagerActions, ConnectionManagerVariables),
        _ => null,
    };

    public static string Escape(string text) => SecurityElement.Escape(text) ?? string.Empty;

    private sealed record Argument(string Name, bool Out, string Variable);

    private sealed record Action(string Name, params Argument[] Arguments);

    private sealed record Variable(string Name, string Type, bool Events = false, string[]? Allowed = null, (int Min, int Max)? Range = null);

    private static Argument In(string name, string variable) => new(name, false, variable);

    private static Argument Out(string name, string variable) => new(name, true, variable);

    private static readonly Argument Instance = In("InstanceID", "A_ARG_TYPE_InstanceID");

    private static readonly Action[] AvTransportActions =
    [
        new("SetAVTransportURI", Instance, In("CurrentURI", "AVTransportURI"), In("CurrentURIMetaData", "AVTransportURIMetaData")),
        new("SetNextAVTransportURI", Instance, In("NextURI", "NextAVTransportURI"), In("NextURIMetaData", "NextAVTransportURIMetaData")),
        new("GetMediaInfo", Instance,
            Out("NrTracks", "NumberOfTracks"), Out("MediaDuration", "CurrentMediaDuration"),
            Out("CurrentURI", "AVTransportURI"), Out("CurrentURIMetaData", "AVTransportURIMetaData"),
            Out("NextURI", "NextAVTransportURI"), Out("NextURIMetaData", "NextAVTransportURIMetaData"),
            Out("PlayMedium", "PlaybackStorageMedium"), Out("RecordMedium", "RecordStorageMedium"),
            Out("WriteStatus", "RecordMediumWriteStatus")),
        new("GetTransportInfo", Instance,
            Out("CurrentTransportState", "TransportState"), Out("CurrentTransportStatus", "TransportStatus"),
            Out("CurrentSpeed", "TransportPlaySpeed")),
        new("GetPositionInfo", Instance,
            Out("Track", "CurrentTrack"), Out("TrackDuration", "CurrentTrackDuration"),
            Out("TrackMetaData", "CurrentTrackMetaData"), Out("TrackURI", "CurrentTrackURI"),
            Out("RelTime", "RelativeTimePosition"), Out("AbsTime", "AbsoluteTimePosition"),
            Out("RelCount", "RelativeCounterPosition"), Out("AbsCount", "AbsoluteCounterPosition")),
        new("GetDeviceCapabilities", Instance,
            Out("PlayMedia", "PossiblePlaybackStorageMedia"), Out("RecMedia", "PossibleRecordStorageMedia"),
            Out("RecQualityModes", "PossibleRecordQualityModes")),
        new("GetTransportSettings", Instance, Out("PlayMode", "CurrentPlayMode"), Out("RecQualityMode", "CurrentRecordQualityMode")),
        new("Stop", Instance),
        new("Play", Instance, In("Speed", "TransportPlaySpeed")),
        new("Pause", Instance),
        new("Seek", Instance, In("Unit", "A_ARG_TYPE_SeekMode"), In("Target", "A_ARG_TYPE_SeekTarget")),
        new("Next", Instance),
        new("Previous", Instance),
        new("SetPlayMode", Instance, In("NewPlayMode", "CurrentPlayMode")),
        new("GetCurrentTransportActions", Instance, Out("Actions", "CurrentTransportActions")),
    ];

    private static readonly Variable[] AvTransportVariables =
    [
        new("TransportState", "string", Allowed: ["STOPPED", "PLAYING", "TRANSITIONING", "PAUSED_PLAYBACK", "NO_MEDIA_PRESENT"]),
        new("TransportStatus", "string", Allowed: ["OK", "ERROR_OCCURRED"]),
        new("PlaybackStorageMedium", "string", Allowed: ["NONE", "NETWORK", "UNKNOWN"]),
        new("RecordStorageMedium", "string", Allowed: ["NOT_IMPLEMENTED"]),
        new("PossiblePlaybackStorageMedia", "string"),
        new("PossibleRecordStorageMedia", "string"),
        new("CurrentPlayMode", "string", Allowed: ["NORMAL"]),
        new("TransportPlaySpeed", "string", Allowed: ["1"]),
        new("RecordMediumWriteStatus", "string", Allowed: ["NOT_IMPLEMENTED"]),
        new("CurrentRecordQualityMode", "string", Allowed: ["NOT_IMPLEMENTED"]),
        new("PossibleRecordQualityModes", "string"),
        new("NumberOfTracks", "ui4", Range: (0, 1)),
        new("CurrentTrack", "ui4", Range: (0, 1)),
        new("CurrentTrackDuration", "string"),
        new("CurrentMediaDuration", "string"),
        new("CurrentTrackMetaData", "string"),
        new("CurrentTrackURI", "string"),
        new("AVTransportURI", "string"),
        new("AVTransportURIMetaData", "string"),
        new("NextAVTransportURI", "string"),
        new("NextAVTransportURIMetaData", "string"),
        new("RelativeTimePosition", "string"),
        new("AbsoluteTimePosition", "string"),
        new("RelativeCounterPosition", "i4"),
        new("AbsoluteCounterPosition", "i4"),
        new("CurrentTransportActions", "string"),
        new("LastChange", "string", Events: true),
        new("A_ARG_TYPE_SeekMode", "string", Allowed: ["REL_TIME", "ABS_TIME", "TRACK_NR"]),
        new("A_ARG_TYPE_SeekTarget", "string"),
        new("A_ARG_TYPE_InstanceID", "ui4"),
    ];

    private static readonly Argument Channel = In("Channel", "A_ARG_TYPE_Channel");

    private static readonly Action[] RenderingControlActions =
    [
        new("ListPresets", Instance, Out("CurrentPresetNameList", "PresetNameList")),
        new("SelectPreset", Instance, In("PresetName", "A_ARG_TYPE_PresetName")),
        new("GetMute", Instance, Channel, Out("CurrentMute", "Mute")),
        new("SetMute", Instance, Channel, In("DesiredMute", "Mute")),
        new("GetVolume", Instance, Channel, Out("CurrentVolume", "Volume")),
        new("SetVolume", Instance, Channel, In("DesiredVolume", "Volume")),
        new("GetVolumeDB", Instance, Channel, Out("CurrentVolume", "VolumeDB")),
        new("SetVolumeDB", Instance, Channel, In("DesiredVolume", "VolumeDB")),
        new("GetVolumeDBRange", Instance, Channel, Out("MinValue", "VolumeDB"), Out("MaxValue", "VolumeDB")),
    ];

    private static readonly Variable[] RenderingControlVariables =
    [
        new("PresetNameList", "string"),
        new("LastChange", "string", Events: true),
        new("Mute", "boolean"),
        new("Volume", "ui2", Range: (0, 100)),
        new("VolumeDB", "i2", Range: (-32768, 32767)),
        new("A_ARG_TYPE_Channel", "string", Allowed: ["Master"]),
        new("A_ARG_TYPE_InstanceID", "ui4"),
        new("A_ARG_TYPE_PresetName", "string", Allowed: ["FactoryDefaults"]),
    ];

    private static readonly Action[] ConnectionManagerActions =
    [
        new("GetProtocolInfo", Out("Source", "SourceProtocolInfo"), Out("Sink", "SinkProtocolInfo")),
        new("GetCurrentConnectionIDs", Out("ConnectionIDs", "CurrentConnectionIDs")),
        new("GetCurrentConnectionInfo", In("ConnectionID", "A_ARG_TYPE_ConnectionID"),
            Out("RcsID", "A_ARG_TYPE_RcsID"), Out("AVTransportID", "A_ARG_TYPE_AVTransportID"),
            Out("ProtocolInfo", "A_ARG_TYPE_ProtocolInfo"), Out("PeerConnectionManager", "A_ARG_TYPE_ConnectionManager"),
            Out("PeerConnectionID", "A_ARG_TYPE_ConnectionID"), Out("Direction", "A_ARG_TYPE_Direction"),
            Out("Status", "A_ARG_TYPE_ConnectionStatus")),
    ];

    private static readonly Variable[] ConnectionManagerVariables =
    [
        new("SourceProtocolInfo", "string", Events: true),
        new("SinkProtocolInfo", "string", Events: true),
        new("CurrentConnectionIDs", "string", Events: true),
        new("A_ARG_TYPE_ConnectionStatus", "string", Allowed: ["OK", "ContentFormatMismatch", "InsufficientBandwidth", "UnreliableChannel", "Unknown"]),
        new("A_ARG_TYPE_ConnectionManager", "string"),
        new("A_ARG_TYPE_Direction", "string", Allowed: ["Input", "Output"]),
        new("A_ARG_TYPE_ProtocolInfo", "string"),
        new("A_ARG_TYPE_ConnectionID", "i4"),
        new("A_ARG_TYPE_AVTransportID", "i4"),
        new("A_ARG_TYPE_RcsID", "i4"),
    ];

    private static string Build(Action[] actions, Variable[] variables)
    {
        var xml = new StringBuilder();
        xml.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        xml.Append("<scpd xmlns=\"urn:schemas-upnp-org:service-1-0\">\n");
        xml.Append("  <specVersion><major>1</major><minor>0</minor></specVersion>\n");
        xml.Append("  <actionList>\n");
        foreach (Action action in actions)
        {
            xml.Append("    <action>\n      <name>").Append(action.Name).Append("</name>\n");
            if (action.Arguments.Length > 0)
            {
                xml.Append("      <argumentList>\n");
                foreach (Argument argument in action.Arguments)
                {
                    xml.Append("        <argument><name>").Append(argument.Name).Append("</name><direction>")
                        .Append(argument.Out ? "out" : "in").Append("</direction><relatedStateVariable>")
                        .Append(argument.Variable).Append("</relatedStateVariable></argument>\n");
                }

                xml.Append("      </argumentList>\n");
            }

            xml.Append("    </action>\n");
        }

        xml.Append("  </actionList>\n  <serviceStateTable>\n");
        foreach (Variable variable in variables)
        {
            xml.Append("    <stateVariable sendEvents=\"").Append(variable.Events ? "yes" : "no").Append("\">")
                .Append("<name>").Append(variable.Name).Append("</name><dataType>").Append(variable.Type).Append("</dataType>");
            if (variable.Allowed is { Length: > 0 } allowed)
            {
                xml.Append("<allowedValueList>");
                foreach (string value in allowed)
                {
                    xml.Append("<allowedValue>").Append(value).Append("</allowedValue>");
                }

                xml.Append("</allowedValueList>");
            }

            if (variable.Range is { } range)
            {
                xml.Append("<allowedValueRange><minimum>").Append(range.Min).Append("</minimum><maximum>")
                    .Append(range.Max).Append("</maximum><step>1</step></allowedValueRange>");
            }

            xml.Append("</stateVariable>\n");
        }

        xml.Append("  </serviceStateTable>\n</scpd>\n");
        return xml.ToString();
    }
}
