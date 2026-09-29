using RaCMAN.Protocol;

namespace RaCMAN.App;

/// <summary>One stretch of addresses a patch writes, from <see cref="Start"/> up to but not including <see cref="End"/>.</summary>
public readonly record struct PatchRange(uint Start, ulong End)
{
    public override string ToString() => $"0x{Start:x8}-0x{End - 1:x8}";
}

/// <summary>
/// Which addresses a patch writes, worked out from nothing but its own words and bytes: four bytes
/// for each word, one for each single byte, and a cave is simply a run of consecutive words. No
/// address is known here; two patches overlap when their stretches share a byte, which is when
/// RPCS3 would apply one over the other and leave neither whole.
/// </summary>
public static class PatchRanges
{
    /// <summary>The stretches <paramref name="words"/> and <paramref name="bytes"/> cover, sorted and with touching ones joined.</summary>
    public static IReadOnlyList<PatchRange> Of(IReadOnlyList<PatchWord> words, IReadOnlyList<PatchByte> bytes)
    {
        var pieces = new List<PatchRange>(words.Count + bytes.Count);
        foreach (var word in words) pieces.Add(new PatchRange(word.Address, (ulong)word.Address + 4));
        foreach (var single in bytes) pieces.Add(new PatchRange(single.Address, (ulong)single.Address + 1));

        pieces.Sort((a, b) => a.Start.CompareTo(b.Start));

        var joined = new List<PatchRange>(pieces.Count);
        foreach (var piece in pieces)
        {
            if (joined.Count > 0 && piece.Start <= joined[^1].End)
            {
                var last = joined[^1];
                joined[^1] = last with { End = Math.Max(last.End, piece.End) };
            }
            else
            {
                joined.Add(piece);
            }
        }

        return joined;
    }

    /// <summary>The first address both lists write, or null when they share none. Both lists come out of <see cref="Of"/>.</summary>
    public static uint? FirstShared(IReadOnlyList<PatchRange> a, IReadOnlyList<PatchRange> b)
    {
        int i = 0;
        int j = 0;
        while (i < a.Count && j < b.Count)
        {
            ulong start = Math.Max(a[i].Start, b[j].Start);
            ulong end = Math.Min(a[i].End, b[j].End);
            if (start < end) return (uint)start;

            if (a[i].End <= b[j].End) i++;
            else j++;
        }

        return null;
    }
}
