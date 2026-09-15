namespace RaCMAN.App.Panels;

/// <summary>
/// One line of the moby inspector's table: the field its box reads and writes, and the text the
/// other columns show beside it. The labels are made once, with the line, because they are drawn
/// every frame and measured every time a row arrives.
/// </summary>
public sealed class MobyInspectorRow
{
    /// <summary>What is read, written and watched: a struct entry, or one float out of a vector.</summary>
    public required MobyField Field { get; init; }

    /// <summary>The Offset column's text.</summary>
    public required string Offset { get; init; }

    /// <summary>The Type column's text, which is the only place a raw block's length is said.</summary>
    public required string Type { get; init; }

    public string Name => Field.Name;

    /// <summary>The Value column's text, as the last row that arrived formats it.</summary>
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// How much room each of the inspector's four columns needs, in whatever unit measured them: the
/// width of the longest thing in it.
/// </summary>
public readonly record struct MobyColumnWidths(float Field, float Offset, float Type, float Value)
{
    public float Total => Field + Offset + Type + Value;
}

/// <summary>
/// What the moby inspector draws, which is not quite the struct the layout file declares: a vector
/// is four floats the game writes one at a time, so it is four lines here - position.x, position.y
/// and the rest - each with its own offset, its own box and its own watch.
/// <para>
/// The files and <see cref="MobyLayout.Struct"/> are left alone. The file is a record of the game's
/// struct and this is a view of it, and the two are checked against each other. Nothing here
/// touches ImGui either - the window measures the strings and draws them, this decides what they
/// are - so all of it can be tested.
/// </para>
/// </summary>
public static class MobyInspectorRows
{
    public const string FieldHeader = "Field";

    public const string OffsetHeader = "Offset";

    public const string TypeHeader = "Type";

    public const string ValueHeader = "Value";

    /// <summary>
    /// What the Value column makes room for when everything in it is shorter: a float with space to
    /// type a longer one over it. A column exactly as wide as "0" is a column nothing can be typed
    /// into.
    /// </summary>
    public const string ValueSample = "-0000.0000";

    /// <summary>The lines for a struct, in the file's own order, every vector split into floats.</summary>
    public static List<MobyInspectorRow> Build(IReadOnlyList<MobyField> fields)
    {
        var rows = new List<MobyInspectorRow>(fields.Count);

        foreach (var field in fields)
        {
            int components = MobyFieldCodec.ComponentCount(field.Type);
            if (components == 0)
            {
                rows.Add(Line(field));
                continue;
            }

            for (int i = 0; i < components; i++)
            {
                // Its own offset and its own f32: the box writes the four bytes of that component
                // alone, and the right-click offers the watch a whole vector could never become.
                rows.Add(Line(new MobyField
                {
                    Name = $"{field.Name}.{MobyFieldCodec.Components[i]}",
                    Offset = field.Offset + (i * 4),
                    Type = "f32",
                    From = field.From,
                }));
            }
        }

        return rows;
    }

    /// <summary>Formats every line again out of the row that has just arrived.</summary>
    public static void Refresh(IReadOnlyList<MobyInspectorRow> rows, ReadOnlySpan<byte> row)
    {
        foreach (var line in rows) line.Value = MobyFieldCodec.Format(line.Field, row);
    }

    /// <summary>
    /// How wide each column has to be to hold its longest entry, header included, in whatever
    /// <paramref name="width"/> measures text in. Measuring is the window's business - it is the
    /// font that decides - and which strings are measured is this list's.
    /// <para>
    /// The four together are held to <paramref name="room"/>, and it is Value that gives way: a raw
    /// block is 191 characters of hex, wider than any screen, and its box is one that scrolls along
    /// where the other three columns are plain text that would be cut off.
    /// </para>
    /// </summary>
    public static MobyColumnWidths Measure(IReadOnlyList<MobyInspectorRow> rows, Func<string, float> width, float room)
    {
        float field = width(FieldHeader);
        float offset = width(OffsetHeader);
        float type = width(TypeHeader);
        float least = MathF.Max(width(ValueHeader), width(ValueSample));
        float value = least;

        foreach (var line in rows)
        {
            field = MathF.Max(field, width(line.Name));
            offset = MathF.Max(offset, width(line.Offset));
            type = MathF.Max(type, width(line.Type));
            value = MathF.Max(value, width(line.Value));
        }

        value = MathF.Max(MathF.Min(value, room - field - offset - type), least);
        return new MobyColumnWidths(field, offset, type, value);
    }

    private static MobyInspectorRow Line(MobyField field) => new()
    {
        Field = field,
        Offset = $"0x{field.Offset:X2}",
        Type = field.Type == "bytes" ? $"bytes[{field.Length}]" : field.Type,
    };
}
