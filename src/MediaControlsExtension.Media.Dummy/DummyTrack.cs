using System.Buffers.Binary;
using System.Globalization;

namespace JPSoftworks.MediaControlsExtension.Media.Dummy;

internal sealed record DummyTrack(string Title, string Artist, string Album, string Genre, TimeSpan Duration, int Color)
{
    private static readonly string[] Adjectives = ["Amber", "Quiet", "Electric", "Silver", "Velvet", "Distant", "Neon", "Golden"];
    private static readonly string[] Nouns = ["Horizon", "Signal", "Garden", "Orbit", "Rain", "River", "Echo", "Sky"];
    private static readonly string[] Artists = ["The Sample Band", "Random Ensemble", "Test Pattern", "Synthetic Waves"];
    private static readonly string[] Genres = ["Electronic", "Jazz", "Ambient", "Rock"];

    public static DummyTrack[] CreatePlaylist(Random random)
    {
        var tracks = new DummyTrack[12];
        for (var i = 0; i < tracks.Length; i++)
        {
            tracks[i] = new(Adjectives[random.Next(Adjectives.Length)] + " " + Nouns[random.Next(Nouns.Length)] +
                " " + (i + 1).ToString(CultureInfo.InvariantCulture), Artists[random.Next(Artists.Length)],
                "Demo collection " + random.Next(1, 10).ToString(CultureInfo.InvariantCulture), Genres[random.Next(Genres.Length)],
                TimeSpan.FromSeconds(random.Next(15, 46)), random.Next(0x1000000));
        }

        return tracks;
    }

    public MediaArtworkContent CreateArtwork()
    {
        const int size = 32;
        const int header = 54;
        var bytes = new byte[header + size * size * 3];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10), header);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), size);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), size);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(28), 24);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(34), bytes.Length - header);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var color = ((x / 8 + y / 8) & 1) == 0 ? this.Color : this.Color ^ 0x7f7f7f;
                var offset = header + (y * size + x) * 3;
                bytes[offset] = (byte)color;
                bytes[offset + 1] = (byte)(color >> 8);
                bytes[offset + 2] = (byte)(color >> 16);
            }
        }

        return new("image/bmp", bytes, null);
    }
}