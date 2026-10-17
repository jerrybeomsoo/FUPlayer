using System.Globalization;

namespace FUPlayer.Core.Metadata;

public static partial class MetadataReader
{
    /// <summary>
    /// A rating stored in the file, as 0 to 5 stars, or null when there is none. Read from the fields players write:
    /// Vorbis comments (FLAC, Ogg) RATING, on a 0-5 scale as JRiver Media Center writes it or 0-100, and FMPS_RATING,
    /// 0-1; ID3v2 (MP3) POPM, 1-255, mapped the way Windows Media Player maps its stars, and a TXXX FMPS_Rating.
    /// </summary>
    private static int? ReadRating(TagLib.File file)
    {
        if (file.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
        {
            if (ParseRating(xiph.GetFirstField("RATING")) is { } rating)
            {
                return Stars(rating, rating <= 5 ? 5 : 100);
            }

            if (ParseRating(xiph.GetFirstField("FMPS_RATING")) is { } fmps)
            {
                return Stars(fmps, 1);
            }
        }

        if (file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
        {
            foreach (TagLib.Id3v2.PopularimeterFrame frame in id3.GetFrames<TagLib.Id3v2.PopularimeterFrame>())
            {
                if (frame.Rating > 0)
                {
                    return frame.Rating switch
                    {
                        < 32 => 1,
                        < 96 => 2,
                        < 160 => 3,
                        < 224 => 4,
                        _ => 5,
                    };
                }
            }

            if (ParseRating(TagLib.Id3v2.UserTextInformationFrame.Get(id3, "FMPS_Rating", false)?.Text.FirstOrDefault()) is { } fmps)
            {
                return Stars(fmps, 1);
            }
        }

        return null;
    }

    private static double? ParseRating(string? text) =>
        double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && value >= 0 ? value : null;

    private static int Stars(double value, double scale) => (int)Math.Clamp(Math.Round(value / scale * 5), 0, 5);
}
