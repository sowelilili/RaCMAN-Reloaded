using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ImGuiNET;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace RaCMAN.App;

/// <summary>
/// The in-repo Dear ImGui backend: font atlas upload, an OpenGL 3.3 core shader, streamed
/// VAO/VBO/EBO, scissor, keyboard/mouse/text/scroll input, clipboard and DPI-aware scaling.
/// </summary>
public sealed class ImGuiController : IDisposable
{
    private const string VertexShaderSource = """
        #version 330 core
        layout (location = 0) in vec2 Position;
        layout (location = 1) in vec2 UV;
        layout (location = 2) in vec4 Color;
        uniform mat4 ProjMtx;
        out vec2 Frag_UV;
        out vec4 Frag_Color;
        void main()
        {
            Frag_UV = UV;
            Frag_Color = Color;
            gl_Position = ProjMtx * vec4(Position.xy, 0, 1);
        }
        """;

    private const string FragmentShaderSource = """
        #version 330 core
        in vec2 Frag_UV;
        in vec4 Frag_Color;
        uniform sampler2D Texture;
        layout (location = 0) out vec4 Out_Color;
        void main()
        {
            Out_Color = Frag_Color * texture(Texture, Frag_UV.st);
        }
        """;

    private static readonly (Keys Key, ImGuiKey Mapped)[] KeyMap = BuildKeyMap();

    // Kept alive for the lifetime of the controller: cimgui holds raw pointers to them.
    private static GetClipboardDelegate? _getClipboard;
    private static SetClipboardDelegate? _setClipboard;
    private static IntPtr _clipboardBuffer;
    private static GameWindow? _clipboardWindow;

    private readonly GameWindow _window;

    private int _vertexArray;
    private int _vertexBuffer;
    private int _indexBuffer;
    private int _vertexBufferSize;
    private int _indexBufferSize;
    private int _shader;
    private int _fontTexture;
    private int _uniformProjection;
    private int _uniformTexture;
    private float _scrollX;
    private float _scrollY;
    private bool _disposed;

    public ImGuiController(GameWindow window)
    {
        _window = window;

        Context = ImGui.CreateContext();
        ImGui.SetCurrentContext(Context);

        var io = ImGui.GetIO();

        // The layout is fixed in code, so there is nothing worth writing to imgui.ini.
        unsafe
        {
            io.NativePtr->IniFilename = null;
        }

        io.Fonts.AddFontDefault();
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;

        InstallClipboard(window);
        CreateDeviceResources();
        ApplyStyle();

        window.TextInput += OnTextInput;
        window.MouseWheel += OnMouseWheel;
    }

    public IntPtr Context { get; }

    private void OnTextInput(OpenTK.Windowing.Common.TextInputEventArgs args) =>
        ImGui.GetIO().AddInputCharacter((uint)args.Unicode);

    private void OnMouseWheel(OpenTK.Windowing.Common.MouseWheelEventArgs args)
    {
        _scrollX += args.OffsetX;
        _scrollY += args.OffsetY;
    }

    private static void InstallClipboard(GameWindow window)
    {
        _clipboardWindow = window;
        _getClipboard = _ =>
        {
            var text = _clipboardWindow?.ClipboardString ?? string.Empty;
            if (_clipboardBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_clipboardBuffer);
            _clipboardBuffer = Marshal.StringToHGlobalAnsi(text);
            return _clipboardBuffer;
        };

        _setClipboard = (_, text) =>
        {
            if (_clipboardWindow is null) return;
            _clipboardWindow.ClipboardString = Marshal.PtrToStringAnsi(text) ?? string.Empty;
        };

        var platform = ImGui.GetPlatformIO();
        platform.Platform_GetClipboardTextFn = Marshal.GetFunctionPointerForDelegate(_getClipboard);
        platform.Platform_SetClipboardTextFn = Marshal.GetFunctionPointerForDelegate(_setClipboard);
    }

    private static void ApplyStyle()
    {
        var style = ImGui.GetStyle();
        style.WindowRounding = 4f;
        style.FrameRounding = 3f;
        style.GrabRounding = 3f;
        style.ScrollbarRounding = 3f;
        style.WindowPadding = new Vector2(10, 10);
        style.FramePadding = new Vector2(6, 4);
        style.ItemSpacing = new Vector2(8, 6);
        style.WindowBorderSize = 0f;
        ImGui.StyleColorsDark();
    }

    // ---------------------------------------------------------------- device resources

    private void CreateDeviceResources()
    {
        _vertexBufferSize = 20000;
        _indexBufferSize = 4000;

        _vertexArray = GL.GenVertexArray();
        _vertexBuffer = GL.GenBuffer();
        _indexBuffer = GL.GenBuffer();

        GL.BindVertexArray(_vertexArray);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
        GL.BufferData(BufferTarget.ArrayBuffer, _vertexBufferSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _indexBuffer);
        GL.BufferData(BufferTarget.ElementArrayBuffer, _indexBufferSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);

        _shader = CompileProgram(VertexShaderSource, FragmentShaderSource);
        _uniformProjection = GL.GetUniformLocation(_shader, "ProjMtx");
        _uniformTexture = GL.GetUniformLocation(_shader, "Texture");

        int stride = Unsafe.SizeOf<ImDrawVert>();
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 8);
        GL.EnableVertexAttribArray(2);
        GL.VertexAttribPointer(2, 4, VertexAttribPointerType.UnsignedByte, true, stride, 16);

        GL.BindVertexArray(0);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, 0);

        RecreateFontTexture();
    }

    private void RecreateFontTexture()
    {
        var io = ImGui.GetIO();
        io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out int width, out int height, out _);

        if (_fontTexture != 0) GL.DeleteTexture(_fontTexture);
        _fontTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _fontTexture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, width, height, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.BindTexture(TextureTarget.Texture2D, 0);

        io.Fonts.SetTexID(_fontTexture);
        io.Fonts.ClearTexData();
    }

    // ---------------------------------------------------------------- user textures

    private readonly HashSet<int> _textures = new();

    /// <summary>
    /// Uploads an RGBA8 image and returns the handle ImGui draw commands take. Must be called on
    /// the render thread, like everything else that touches GL.
    /// </summary>
    public int CreateTexture(int width, int height, ReadOnlySpan<byte> rgba)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "texture is empty");
        if (rgba.Length < width * height * 4) throw new ArgumentException("RGBA data is short", nameof(rgba));

        int texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);

        unsafe
        {
            fixed (byte* pixels = rgba)
            {
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, width, height, 0,
                    PixelFormat.Rgba, PixelType.UnsignedByte, (IntPtr)pixels);
            }
        }

        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.BindTexture(TextureTarget.Texture2D, 0);

        _textures.Add(texture);
        return texture;
    }

    public void DeleteTexture(int texture)
    {
        if (texture == 0 || !_textures.Remove(texture)) return;
        GL.DeleteTexture(texture);
    }

    private static int CompileProgram(string vertexSource, string fragmentSource)
    {
        int vertex = CompileShader(ShaderType.VertexShader, vertexSource);
        int fragment = CompileShader(ShaderType.FragmentShader, fragmentSource);

        int program = GL.CreateProgram();
        GL.AttachShader(program, vertex);
        GL.AttachShader(program, fragment);
        GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linked);
        if (linked == 0) throw new InvalidOperationException($"ImGui shader link failed: {GL.GetProgramInfoLog(program)}");

        GL.DetachShader(program, vertex);
        GL.DetachShader(program, fragment);
        GL.DeleteShader(vertex);
        GL.DeleteShader(fragment);
        return program;
    }

    private static int CompileShader(ShaderType type, string source)
    {
        int shader = GL.CreateShader(type);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out int compiled);
        if (compiled == 0) throw new InvalidOperationException($"ImGui {type} failed: {GL.GetShaderInfoLog(shader)}");
        return shader;
    }

    // ---------------------------------------------------------------- per-frame

    public void Update(float deltaSeconds)
    {
        var io = ImGui.GetIO();

        var client = _window.ClientSize;
        var framebuffer = _window.FramebufferSize;
        io.DisplaySize = new Vector2(client.X, client.Y);
        io.DisplayFramebufferScale = client.X > 0 && client.Y > 0
            ? new Vector2((float)framebuffer.X / client.X, (float)framebuffer.Y / client.Y)
            : Vector2.One;
        io.DeltaTime = deltaSeconds > 0 ? deltaSeconds : 1f / 60f;

        UpdateInput(io);
        ImGui.NewFrame();
    }

    private void UpdateInput(ImGuiIOPtr io)
    {
        var mouse = _window.MouseState;
        io.AddMousePosEvent(mouse.X, mouse.Y);
        io.AddMouseButtonEvent(0, mouse[MouseButton.Left]);
        io.AddMouseButtonEvent(1, mouse[MouseButton.Right]);
        io.AddMouseButtonEvent(2, mouse[MouseButton.Middle]);

        if (_scrollX != 0 || _scrollY != 0)
        {
            io.AddMouseWheelEvent(_scrollX, _scrollY);
            _scrollX = 0;
            _scrollY = 0;
        }

        var keyboard = _window.KeyboardState;
        foreach (var (key, mapped) in KeyMap)
        {
            io.AddKeyEvent(mapped, keyboard.IsKeyDown(key));
        }

        io.AddKeyEvent(ImGuiKey.ModCtrl, keyboard.IsKeyDown(Keys.LeftControl) || keyboard.IsKeyDown(Keys.RightControl));
        io.AddKeyEvent(ImGuiKey.ModShift, keyboard.IsKeyDown(Keys.LeftShift) || keyboard.IsKeyDown(Keys.RightShift));
        io.AddKeyEvent(ImGuiKey.ModAlt, keyboard.IsKeyDown(Keys.LeftAlt) || keyboard.IsKeyDown(Keys.RightAlt));
        io.AddKeyEvent(ImGuiKey.ModSuper, keyboard.IsKeyDown(Keys.LeftSuper) || keyboard.IsKeyDown(Keys.RightSuper));
    }

    public void Render()
    {
        ImGui.Render();
        RenderDrawData(ImGui.GetDrawData());
    }

    private void RenderDrawData(ImDrawDataPtr drawData)
    {
        if (drawData.CmdListsCount == 0) return;

        var framebuffer = _window.FramebufferSize;
        if (framebuffer.X <= 0 || framebuffer.Y <= 0) return;

        int vertexStride = Unsafe.SizeOf<ImDrawVert>();

        GL.Enable(EnableCap.Blend);
        GL.BlendEquation(BlendEquationMode.FuncAdd);
        GL.BlendFuncSeparate(BlendingFactorSrc.SrcAlpha, BlendingFactorDest.OneMinusSrcAlpha,
            BlendingFactorSrc.One, BlendingFactorDest.OneMinusSrcAlpha);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.StencilTest);
        GL.Enable(EnableCap.ScissorTest);

        GL.Viewport(0, 0, framebuffer.X, framebuffer.Y);

        var position = drawData.DisplayPos;
        var size = drawData.DisplaySize;
        var projection = Matrix4x4.CreateOrthographicOffCenter(
            position.X, position.X + size.X, position.Y + size.Y, position.Y, -1f, 1f);

        GL.UseProgram(_shader);
        GL.UniformMatrix4(_uniformProjection, 1, false, ref projection.M11);
        GL.Uniform1(_uniformTexture, 0);
        GL.ActiveTexture(TextureUnit.Texture0);

        GL.BindVertexArray(_vertexArray);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _indexBuffer);

        var scale = drawData.FramebufferScale;

        for (int n = 0; n < drawData.CmdListsCount; n++)
        {
            var cmdList = drawData.CmdLists[n];

            int vertexBytes = cmdList.VtxBuffer.Size * vertexStride;
            if (vertexBytes > _vertexBufferSize)
            {
                _vertexBufferSize = Math.Max(_vertexBufferSize * 2, vertexBytes);
                GL.BufferData(BufferTarget.ArrayBuffer, _vertexBufferSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);
            }

            int indexBytes = cmdList.IdxBuffer.Size * sizeof(ushort);
            if (indexBytes > _indexBufferSize)
            {
                _indexBufferSize = Math.Max(_indexBufferSize * 2, indexBytes);
                GL.BufferData(BufferTarget.ElementArrayBuffer, _indexBufferSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);
            }

            GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, vertexBytes, cmdList.VtxBuffer.Data);
            GL.BufferSubData(BufferTarget.ElementArrayBuffer, IntPtr.Zero, indexBytes, cmdList.IdxBuffer.Data);

            for (int i = 0; i < cmdList.CmdBuffer.Size; i++)
            {
                var command = cmdList.CmdBuffer[i];
                if (command.UserCallback != IntPtr.Zero) continue;

                var clip = command.ClipRect;
                float x0 = (clip.X - position.X) * scale.X;
                float y0 = (clip.Y - position.Y) * scale.Y;
                float x1 = (clip.Z - position.X) * scale.X;
                float y1 = (clip.W - position.Y) * scale.Y;
                if (x1 <= x0 || y1 <= y0) continue;

                GL.Scissor((int)x0, framebuffer.Y - (int)y1, (int)(x1 - x0), (int)(y1 - y0));
                GL.BindTexture(TextureTarget.Texture2D, (int)command.TextureId);
                GL.DrawElementsBaseVertex(
                    PrimitiveType.Triangles,
                    (int)command.ElemCount,
                    DrawElementsType.UnsignedShort,
                    (IntPtr)(command.IdxOffset * sizeof(ushort)),
                    (int)command.VtxOffset);
            }
        }

        GL.BindVertexArray(0);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, 0);
        GL.UseProgram(0);
        GL.Disable(EnableCap.ScissorTest);
    }

    // ---------------------------------------------------------------- keys

    private static (Keys, ImGuiKey)[] BuildKeyMap()
    {
        var map = new List<(Keys, ImGuiKey)>
        {
            (Keys.Tab, ImGuiKey.Tab),
            (Keys.Left, ImGuiKey.LeftArrow),
            (Keys.Right, ImGuiKey.RightArrow),
            (Keys.Up, ImGuiKey.UpArrow),
            (Keys.Down, ImGuiKey.DownArrow),
            (Keys.PageUp, ImGuiKey.PageUp),
            (Keys.PageDown, ImGuiKey.PageDown),
            (Keys.Home, ImGuiKey.Home),
            (Keys.End, ImGuiKey.End),
            (Keys.Insert, ImGuiKey.Insert),
            (Keys.Delete, ImGuiKey.Delete),
            (Keys.Backspace, ImGuiKey.Backspace),
            (Keys.Space, ImGuiKey.Space),
            (Keys.Enter, ImGuiKey.Enter),
            (Keys.Escape, ImGuiKey.Escape),
            (Keys.KeyPadEnter, ImGuiKey.KeypadEnter),
            (Keys.Apostrophe, ImGuiKey.Apostrophe),
            (Keys.Comma, ImGuiKey.Comma),
            (Keys.Minus, ImGuiKey.Minus),
            (Keys.Period, ImGuiKey.Period),
            (Keys.Slash, ImGuiKey.Slash),
            (Keys.Semicolon, ImGuiKey.Semicolon),
            (Keys.Equal, ImGuiKey.Equal),
            (Keys.LeftBracket, ImGuiKey.LeftBracket),
            (Keys.Backslash, ImGuiKey.Backslash),
            (Keys.RightBracket, ImGuiKey.RightBracket),
            (Keys.GraveAccent, ImGuiKey.GraveAccent),
            (Keys.CapsLock, ImGuiKey.CapsLock),
            (Keys.ScrollLock, ImGuiKey.ScrollLock),
            (Keys.NumLock, ImGuiKey.NumLock),
            (Keys.PrintScreen, ImGuiKey.PrintScreen),
            (Keys.Pause, ImGuiKey.Pause),
            (Keys.LeftControl, ImGuiKey.LeftCtrl),
            (Keys.LeftShift, ImGuiKey.LeftShift),
            (Keys.LeftAlt, ImGuiKey.LeftAlt),
            (Keys.LeftSuper, ImGuiKey.LeftSuper),
            (Keys.RightControl, ImGuiKey.RightCtrl),
            (Keys.RightShift, ImGuiKey.RightShift),
            (Keys.RightAlt, ImGuiKey.RightAlt),
            (Keys.RightSuper, ImGuiKey.RightSuper),
            (Keys.Menu, ImGuiKey.Menu),
        };

        for (int i = 0; i <= 9; i++) map.Add((Keys.D0 + i, ImGuiKey._0 + i));
        for (int i = 0; i <= 9; i++) map.Add((Keys.KeyPad0 + i, ImGuiKey.Keypad0 + i));
        for (int i = 0; i < 26; i++) map.Add((Keys.A + i, ImGuiKey.A + i));
        for (int i = 0; i < 12; i++) map.Add((Keys.F1 + i, ImGuiKey.F1 + i));

        map.Add((Keys.KeyPadDecimal, ImGuiKey.KeypadDecimal));
        map.Add((Keys.KeyPadDivide, ImGuiKey.KeypadDivide));
        map.Add((Keys.KeyPadMultiply, ImGuiKey.KeypadMultiply));
        map.Add((Keys.KeyPadSubtract, ImGuiKey.KeypadSubtract));
        map.Add((Keys.KeyPadAdd, ImGuiKey.KeypadAdd));
        map.Add((Keys.KeyPadEqual, ImGuiKey.KeypadEqual));

        return map.ToArray();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _window.TextInput -= OnTextInput;
        _window.MouseWheel -= OnMouseWheel;

        foreach (int texture in _textures) GL.DeleteTexture(texture);
        _textures.Clear();

        if (_fontTexture != 0) GL.DeleteTexture(_fontTexture);
        if (_vertexBuffer != 0) GL.DeleteBuffer(_vertexBuffer);
        if (_indexBuffer != 0) GL.DeleteBuffer(_indexBuffer);
        if (_vertexArray != 0) GL.DeleteVertexArray(_vertexArray);
        if (_shader != 0) GL.DeleteProgram(_shader);

        ImGui.DestroyContext(Context);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetClipboardDelegate(IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetClipboardDelegate(IntPtr context, IntPtr text);
}
