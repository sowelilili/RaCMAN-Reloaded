using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using ImGuiNET;

namespace RaCMAN.App.Panels;

/// <summary>
/// The Lua console's editor (2026-10-31): ImGui's own multi-line text box, so typing, selection,
/// the clipboard and undo are ImGui's, with a line-number gutter beside it and the text drawn over
/// it in colour (<see cref="LuaSyntax"/>). The approach is the dl-tas script editor's.
/// <para>
/// How the colour works. The text box is made as tall and as wide as the whole script, inside a
/// child window of ours that does the scrolling, so the box never scrolls itself and every line
/// sits at a place this class can work out: the box's corner plus the frame padding plus
/// line * font size. The box draws its text in a transparent colour; this class draws the same
/// text in token colours over it, and the caret, which ImGui would draw in that transparent
/// colour too. The selection is still ImGui's. The client's font, ProggyClean, is monospaced and
/// pixel-snapped, so the two layouts agree to the pixel. When the caret moves the child is
/// scrolled to keep it in view.
/// </para>
/// <para>
/// Ctrl+Enter is the box's own "done" key (a multi-line box ends its edit on it rather than
/// adding a line), which <see cref="Draw"/> reports as <see cref="RunRequested"/> and follows by
/// giving the box the keyboard back, so the caret stays where it was.
/// </para>
/// </summary>
public sealed unsafe class LuaEditor : IDisposable
{
    private const int MinimumCapacity = 16 * 1024;

    private const ImGuiInputTextFlags Flags = ImGuiInputTextFlags.AllowTabInput
                                              | ImGuiInputTextFlags.CallbackAlways
                                              | ImGuiInputTextFlags.CallbackResize
                                              | ImGuiInputTextFlags.NoHorizontalScroll;

    // Kept in a field: native code holds a pointer to it for as long as the box exists.
    private readonly ImGuiInputTextCallback _callback;

    private IntPtr _buffer;
    private int _capacity;
    private string _text = string.Empty;
    private int[] _lineStarts = { 0 };
    private LuaSyntax.State[] _states = { LuaSyntax.State.Plain };
    private bool _ascii = true;
    private bool _measure = true;
    private float _widest;

    private int _generation;
    private byte[] _label = Array.Empty<byte>();

    private int _cursorByte;
    private int _lastCursorByte = -1;
    private bool _focusRequested;
    private bool _follow;
    private double _blinkFrom;
    private int _focusFrames;

    public LuaEditor()
    {
        _callback = OnCallback;
        _capacity = MinimumCapacity;
        _buffer = Marshal.AllocHGlobal(_capacity);
        *(byte*)_buffer = 0;
        SetText(string.Empty);
    }

    /// <summary>The script, with LF line endings.</summary>
    public string Text => _text;

    public int LineCount => _lineStarts.Length;

    /// <summary>The caret's line, 1-based.</summary>
    public int CursorLine { get; private set; } = 1;

    /// <summary>The caret's column, 1-based.</summary>
    public int CursorColumn { get; private set; } = 1;

    /// <summary>True while the text box has the keyboard.</summary>
    public bool Focused { get; private set; }

    /// <summary>True for the one frame Ctrl+Enter was pressed in the box.</summary>
    public bool RunRequested { get; private set; }

    /// <summary>Replaces the whole text: a new document, with ImGui's undo history started afresh.</summary>
    public void SetText(string text)
    {
        _text = LuaScriptStore.Normalise(text ?? string.Empty);
        var bytes = Encoding.UTF8.GetBytes(_text);
        EnsureCapacity(bytes.Length + 1);
        Marshal.Copy(bytes, 0, _buffer, bytes.Length);
        ((byte*)_buffer)[bytes.Length] = 0;

        // A new ID is a new ImGui text state: no undo back into the previous document.
        _generation++;
        _label = Encoding.UTF8.GetBytes($"##lua{_generation}\0");
        _cursorByte = 0;
        _lastCursorByte = -1;
        CursorLine = 1;
        CursorColumn = 1;
        Reindex();
    }

    public void Focus() => _focusRequested = true;

    // ------------------------------------------------------------------ the colours

    private static Vector4 Background => Ui.Light ? new(1f, 1f, 1f, 1f) : new(0.11f, 0.11f, 0.13f, 1f);
    private static Vector4 GutterText => Ui.Light ? new(0.62f, 0.62f, 0.66f, 1f) : new(0.45f, 0.45f, 0.50f, 1f);
    private static Vector4 CurrentLine => Ui.Light ? new(0.20f, 0.35f, 0.70f, 0.06f) : new(1f, 1f, 1f, 0.05f);
    private static Vector4 CodeText => Ui.Light ? new(0.10f, 0.10f, 0.13f, 1f) : new(0.86f, 0.86f, 0.90f, 1f);
    private static Vector4 Comment => Ui.Light ? new(0.40f, 0.50f, 0.40f, 1f) : new(0.50f, 0.62f, 0.50f, 1f);
    private static Vector4 Keyword => Ui.Light ? new(0.55f, 0.15f, 0.60f, 1f) : new(0.80f, 0.55f, 0.90f, 1f);
    private static Vector4 Builtin => Ui.Light ? new(0.10f, 0.35f, 0.75f, 1f) : new(0.50f, 0.72f, 1.00f, 1f);
    private static Vector4 Number => Ui.Light ? new(0.70f, 0.35f, 0.00f, 1f) : new(0.95f, 0.70f, 0.40f, 1f);
    private static Vector4 StringText => Ui.Light ? new(0.10f, 0.50f, 0.15f, 1f) : new(0.55f, 0.85f, 0.45f, 1f);

    private static Vector4 Colour(LuaSyntax.Kind kind) => kind switch
    {
        LuaSyntax.Kind.Keyword => Keyword,
        LuaSyntax.Kind.Builtin => Builtin,
        LuaSyntax.Kind.Number => Number,
        LuaSyntax.Kind.String => StringText,
        LuaSyntax.Kind.Comment => Comment,
        _ => CodeText,
    };

    // ------------------------------------------------------------------ drawing

    /// <summary>Draws the gutter and the text in <paramref name="size"/>. Returns true when the text changed this frame.</summary>
    public bool Draw(Vector2 size)
    {
        RunRequested = false;
        var font = ImGui.GetFont();
        var style = ImGui.GetStyle();
        float lineHeight = ImGui.GetFontSize();
        float scale = font.FontSize > 0 ? ImGui.GetFontSize() / font.FontSize : 1f;
        var pad = style.FramePadding;
        if (_measure) Measure();

        int digits = Math.Max(2, LineCount.ToString(CultureInfo.InvariantCulture).Length);
        float gutter = ImGui.CalcTextSize(new string('9', digits)).X + pad.X * 2 + 6;

        var gutterMin = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(gutter, size.Y));
        ImGui.SameLine(0, 0);

        ImGui.PushStyleColor(ImGuiCol.ChildBg, Background);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        // Not NoNav: SetKeyboardFocusHere goes through the nav system.
        ImGui.BeginChild("##luapane", new Vector2(Math.Max(1f, size.X - gutter), size.Y), ImGuiChildFlags.None,
            ImGuiWindowFlags.HorizontalScrollbar);
        ImGui.PopStyleVar();

        float scrollX = ImGui.GetScrollX();
        float scrollY = ImGui.GetScrollY();
        var view = ImGui.GetContentRegionAvail();
        var origin = ImGui.GetCursorScreenPos();
        var textOrigin = origin + pad;

        float width = Math.Max(view.X, _widest + pad.X * 2 + lineHeight * 4);
        float height = Math.Max(view.Y, (LineCount + 1) * lineHeight + pad.Y * 2);

        int first = Math.Clamp((int)((scrollY - pad.Y) / lineHeight), 0, LineCount - 1);
        int last = Math.Clamp((int)((scrollY + view.Y) / lineHeight) + 1, 0, LineCount - 1);

        var draw = ImGui.GetWindowDrawList();
        if (Focused)
        {
            float left = ImGui.GetWindowPos().X;
            float right = left + Math.Max(ImGui.GetWindowWidth(), width);
            float y = textOrigin.Y + (CursorLine - 1) * lineHeight;
            draw.AddRectFilled(new Vector2(left, y), new Vector2(right, y + lineHeight), ImGui.GetColorU32(CurrentLine));
        }

        if (_focusRequested)
        {
            ImGui.SetKeyboardFocusHere();
            _focusRequested = false;
            _focusFrames = 4;
        }

        // SetKeyboardFocusHere is a tabbing request, and ImGui will not activate a box that takes
        // Tab from a tab: the box is left focused and inactive. So the box does not take Tab for
        // the few frames the request needs to land.
        var flags = Flags;
        if (_focusFrames > 0)
        {
            _focusFrames = Focused ? 0 : _focusFrames - 1;
            if (_focusFrames > 0) flags &= ~ImGuiInputTextFlags.AllowTabInput;
        }

        bool wasFocused = Focused;
        bool ctrlEnter = ImGui.GetIO().KeyCtrl
                         && (ImGui.IsKeyPressed(ImGuiKey.Enter, false) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false));

        // The box, drawn but not seen: no frame, no text colour.
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Text, Vector4.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f);
        bool edited;
        fixed (byte* label = _label)
        {
            edited = ImGuiNative.igInputTextMultiline(label, (byte*)_buffer, (uint)_capacity, new Vector2(width, height),
                flags, _callback, null) != 0;
        }

        ImGui.PopStyleVar();
        ImGui.PopStyleColor(2);

        Focused = ImGui.IsItemActive();
        if (wasFocused && ctrlEnter)
        {
            RunRequested = true;
            if (!Focused) _focusRequested = true;
        }

        if (edited)
        {
            _text = Marshal.PtrToStringUTF8(_buffer) ?? string.Empty;
            Reindex();
            last = Math.Min(last + 1, LineCount - 1);
            first = Math.Min(first, last);
        }

        UpdateCursor(edited);

        for (int line = first; line <= last; line++)
        {
            DrawLine(draw, font, scale, new Vector2(textOrigin.X, textOrigin.Y + line * lineHeight), line);
        }

        float caretX = CaretOffset(font, scale);
        if (Focused && ((ImGui.GetTime() - _blinkFrom) % 1.2) <= 0.8)
        {
            float x = textOrigin.X + caretX;
            float y = textOrigin.Y + (CursorLine - 1) * lineHeight;
            draw.AddLine(new Vector2(x, y), new Vector2(x, y + lineHeight), ImGui.GetColorU32(CodeText), 1f);
        }

        if (_follow)
        {
            Follow(pad, lineHeight, caretX, scrollX, scrollY, view);
            _follow = false;
        }

        ImGui.EndChild();
        ImGui.PopStyleColor();

        DrawGutter(gutterMin, new Vector2(gutter, size.Y), textOrigin.Y, lineHeight, first, last, pad);
        return edited;
    }

    private void DrawGutter(Vector2 min, Vector2 size, float textTop, float lineHeight, int first, int last, Vector2 pad)
    {
        var draw = ImGui.GetWindowDrawList();
        var max = min + size;
        draw.PushClipRect(min, max, true);
        draw.AddRectFilled(min, max, ImGui.GetColorU32(Background));

        for (int line = first; line <= last; line++)
        {
            string number = (line + 1).ToString(CultureInfo.InvariantCulture);
            var colour = Focused && line == CursorLine - 1 ? CodeText : GutterText;
            float y = textTop + line * lineHeight;
            float x = max.X - pad.X - 2 - ImGui.CalcTextSize(number).X;
            draw.AddText(new Vector2(x, y), ImGui.GetColorU32(colour), number);
        }

        draw.PopClipRect();
    }

    /// <summary>One line in token colours.</summary>
    private void DrawLine(ImDrawListPtr draw, ImFontPtr font, float scale, Vector2 position, int line)
    {
        string text = Line(line);
        if (text.Length == 0) return;

        var state = _states[line];
        float x = 0;
        int index = 0;
        foreach (var (from, length, kind) in LuaSyntax.Tokens(text, ref state))
        {
            while (index < from) x += font.GetCharAdvance(text[index++]) * scale;

            float at = x;
            while (index < from + length) x += font.GetCharAdvance(text[index++]) * scale;
            draw.AddText(new Vector2(position.X + at, position.Y), ImGui.GetColorU32(Colour(kind)), text.Substring(from, length));
        }
    }

    /// <summary>Scrolls the pane so the caret is in view, with a little room around it.</summary>
    private void Follow(Vector2 pad, float lineHeight, float caretX, float scrollX, float scrollY, Vector2 view)
    {
        float top = pad.Y + (CursorLine - 1) * lineHeight;
        if (top - lineHeight < scrollY) ImGui.SetScrollY(Math.Max(0, top - lineHeight));
        else if (top + lineHeight * 2 > scrollY + view.Y) ImGui.SetScrollY(top + lineHeight * 2 - view.Y);

        float x = pad.X + caretX;
        float margin = lineHeight * 3;
        if (x - margin < scrollX) ImGui.SetScrollX(Math.Max(0, x - margin));
        else if (x + margin > scrollX + view.X) ImGui.SetScrollX(x + margin - view.X);
    }

    // ------------------------------------------------------------------ the text

    private string Line(int line)
    {
        int start = _lineStarts[line];
        int end = line + 1 < _lineStarts.Length ? _lineStarts[line + 1] - 1 : _text.Length;
        return _text[start..end];
    }

    private void Reindex()
    {
        var starts = new List<int> { 0 };
        bool ascii = true;
        for (int i = 0; i < _text.Length; i++)
        {
            char c = _text[i];
            if (c == '\n') starts.Add(i + 1);
            else if (c > 127) ascii = false;
        }

        _lineStarts = starts.ToArray();
        _ascii = ascii;
        _measure = true;

        var lines = new string[_lineStarts.Length];
        for (int i = 0; i < lines.Length; i++) lines[i] = Line(i);
        _states = LuaSyntax.LineStates(lines);
    }

    /// <summary>The widest line, for the box's width.</summary>
    private void Measure()
    {
        float widest = 0;
        for (int line = 0; line < _lineStarts.Length; line++)
        {
            string text = Line(line);
            if (text.Length == 0) continue;
            widest = Math.Max(widest, ImGui.CalcTextSize(text).X);
        }

        _widest = widest;
        _measure = false;
    }

    private void UpdateCursor(bool edited)
    {
        if (_cursorByte == _lastCursorByte && !edited) return;
        if (_cursorByte != _lastCursorByte) _follow = true;
        _lastCursorByte = _cursorByte;
        _blinkFrom = ImGui.GetTime();

        int character = _ascii ? _cursorByte : CharIndex(_cursorByte);
        character = Math.Clamp(character, 0, _text.Length);

        int line = Array.BinarySearch(_lineStarts, character);
        if (line < 0) line = ~line - 1;
        line = Math.Clamp(line, 0, _lineStarts.Length - 1);

        CursorLine = line + 1;
        CursorColumn = character - _lineStarts[line] + 1;
    }

    private int CharIndex(int bytes)
    {
        bytes = Math.Clamp(bytes, 0, _capacity - 1);
        return Encoding.UTF8.GetCharCount((byte*)_buffer, bytes);
    }

    private float CaretOffset(ImFontPtr font, float scale)
    {
        int start = _lineStarts[CursorLine - 1];
        int end = Math.Min(start + CursorColumn - 1, _text.Length);
        float x = 0;
        for (int i = start; i < end; i++) x += font.GetCharAdvance(_text[i]) * scale;
        return x;
    }

    // ------------------------------------------------------------------ the callback

    private int OnCallback(ImGuiInputTextCallbackData* data)
    {
        if (data->EventFlag == ImGuiInputTextFlags.CallbackResize)
        {
            if (data->BufSize > _capacity)
            {
                int grown = Math.Max(data->BufSize, _capacity * 2);
                var buffer = Marshal.AllocHGlobal(grown);
                Buffer.MemoryCopy((void*)_buffer, (void*)buffer, grown, _capacity);
                Marshal.FreeHGlobal(_buffer);
                _buffer = buffer;
                _capacity = grown;
                data->Buf = (byte*)buffer;
                data->BufSize = grown;
            }

            return 0;
        }

        if (data->EventFlag == ImGuiInputTextFlags.CallbackAlways) _cursorByte = data->CursorPos;
        return 0;
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _capacity) return;
        int grown = Math.Max(needed, _capacity * 2);
        Marshal.FreeHGlobal(_buffer);
        _buffer = Marshal.AllocHGlobal(grown);
        _capacity = grown;
    }

    public void Dispose()
    {
        if (_buffer == IntPtr.Zero) return;
        Marshal.FreeHGlobal(_buffer);
        _buffer = IntPtr.Zero;
    }
}
