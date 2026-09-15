using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace RaCMAN.App.Panels;

/// <summary>
/// The moby inspector's arithmetic: one field of a row turned into the text its box shows, and that
/// text turned back into the big-endian bytes MEM_WRITE takes. The scalar half is
/// <see cref="WatchValueCodec"/>'s, so a field is read and written by the same parser as a watch of
/// the same shape; only the types a watch has no word for - the vectors and the raw blocks - are
/// this class's own.
/// <para>
/// Nothing here touches ImGui, which is the point: the window owns the boxes and this owns the
/// bytes, and the bytes are what can be tested.
/// </para>
/// </summary>
public static class MobyFieldCodec
{
    /// <summary>Why a field that is not 1, 2 or 4 bytes wide cannot become a watch.</summary>
    public const string WatchSizes = "A watch is 1, 2 or 4 bytes";

    /// <summary>The four components of a vector, in the order the game stores them.</summary>
    public static readonly string[] Components = { "x", "y", "z", "w" };

    /// <summary>One number, in a box: everything but the vectors and the raw blocks.</summary>
    public static bool IsScalar(string type) =>
        type is "u8" or "i8" or "u16" or "i16" or "u32" or "i32" or "u64" or "f32" or "ptr";

    public static bool IsVector(string type) => type is "vec3f" or "vec4f";

    /// <summary>How many floats a vector type holds, or 0 for anything else.</summary>
    public static int ComponentCount(string type) => type switch
    {
        "vec3f" => 3,
        "vec4f" => 4,
        _ => 0,
    };

    /// <summary>A raw block is shown but never written: the client has no idea what is in it.</summary>
    public static bool IsEditable(string type) => IsScalar(type) || IsVector(type);

    /// <summary>
    /// The watch size for a field, or 0 when no watch covers it. A watch is 1, 2 or 4 bytes, so the
    /// 8-byte fields and the raw blocks have none; a vector's components are watched one at a time.
    /// </summary>
    public static byte WatchSize(MobyField field) =>
        IsScalar(field.Type) && field.Length is 1 or 2 or 4 ? (byte)field.Length : (byte)0;

    /// <summary>
    /// Which of <see cref="Ui.FormatValue"/>'s formats a type is read and written in: signed
    /// decimal for the signed integers, a float for f32, hex for a pointer and for the widest
    /// fields, plain decimal for the rest.
    /// </summary>
    public static string FormatFor(string type) => type switch
    {
        "i8" or "i16" or "i32" => "signed",
        "f32" => "float",
        "ptr" or "u64" => "hex",
        _ => "dec",
    };

    /// <summary>The raw bits of a scalar field, big-endian, without deciding what they mean.</summary>
    public static bool TryReadScalar(MobyField field, ReadOnlySpan<byte> row, out ulong value)
    {
        value = 0;
        if (!IsScalar(field.Type) || !Fits(field, row)) return false;

        var span = row[field.Offset..];
        value = field.Length switch
        {
            1 => span[0],
            2 => BinaryPrimitives.ReadUInt16BigEndian(span),
            4 => BinaryPrimitives.ReadUInt32BigEndian(span),
            _ => BinaryPrimitives.ReadUInt64BigEndian(span),
        };

        return true;
    }

    /// <summary>What a pointer field points at, for the inspector's jump to the viewer.</summary>
    public static bool TryReadPointer(MobyField field, ReadOnlySpan<byte> row, out uint target)
    {
        target = 0;
        if (field.Type != "ptr" || !TryReadScalar(field, row, out ulong value)) return false;

        target = (uint)value;
        return true;
    }

    public static bool TryReadComponent(MobyField field, ReadOnlySpan<byte> row, int component, out float value)
    {
        value = 0;
        if (component < 0 || component >= ComponentCount(field.Type) || !Fits(field, row)) return false;

        value = BinaryPrimitives.ReadSingleBigEndian(row[(field.Offset + (component * 4))..]);
        return true;
    }

    /// <summary>
    /// The field as the inspector shows it: a number in the type's own format, a vector as its
    /// floats separated by commas, a raw block as hex. Empty for a field the row is too short for,
    /// which the window draws as a dash.
    /// </summary>
    public static string Format(MobyField field, ReadOnlySpan<byte> row)
    {
        if (!Fits(field, row)) return string.Empty;

        if (IsScalar(field.Type))
        {
            return TryReadScalar(field, row, out ulong value)
                ? Ui.FormatValue(value, (byte)field.Length, FormatFor(field.Type))
                : string.Empty;
        }

        if (IsVector(field.Type))
        {
            var parts = new string[ComponentCount(field.Type)];
            for (int i = 0; i < parts.Length; i++)
            {
                TryReadComponent(field, row, i, out float component);
                parts[i] = component.ToString("0.####", CultureInfo.InvariantCulture);
            }

            return string.Join(", ", parts);
        }

        if (field.Type != "bytes") return string.Empty;

        var text = new StringBuilder(field.Length * 3);
        for (int i = 0; i < field.Length; i++)
        {
            if (i > 0) text.Append(' ');
            text.Append(row[field.Offset + i].ToString("X2"));
        }

        return text.ToString();
    }

    /// <summary>
    /// The box's text as the bytes that go on the wire, or false for text the type cannot hold.
    /// What the field shows is what it takes back, in every type that is editable at all.
    /// </summary>
    public static bool TryEncode(MobyField field, string? text, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        string typed = (text ?? string.Empty).Trim();
        if (typed.Length == 0) return false;

        if (IsScalar(field.Type))
        {
            byte size = (byte)field.Length;
            if (!WatchValueCodec.TryParse(typed, size, FormatFor(field.Type), out ulong value)) return false;

            bytes = WatchValueCodec.Encode(value, size);
            return true;
        }

        int count = ComponentCount(field.Type);
        if (count == 0) return false;

        // A vector is typed the way it is shown, so a comma is a separator and so is a space: what
        // the box puts up has both in it.
        var parts = typed.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries
                                                          | StringSplitOptions.TrimEntries);
        if (parts.Length != count) return false;

        var written = new byte[count * 4];
        for (int i = 0; i < count; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float component))
            {
                return false;
            }

            BinaryPrimitives.WriteSingleBigEndian(written.AsSpan(i * 4), component);
        }

        bytes = written;
        return true;
    }

    /// <summary>Whether the row the console handed back is long enough to hold the field.</summary>
    private static bool Fits(MobyField field, ReadOnlySpan<byte> row) =>
        field.Length > 0 && field.Offset >= 0 && field.Offset + field.Length <= row.Length;
}
