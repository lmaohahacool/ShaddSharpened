using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Windowing;
using Shaft.Engine.Game;

namespace Shaft.Game.Input;

/// <summary>Discrete key actions the host turns into game/UI behaviour.</summary>
public enum UiKey
{
    Escape, Inventory, GamemodeMenu, Music, Chat, ChatSlash, Save, Reload,
    Tab, Enter, Backspace, Delete, Left, Right, Home, End,
}

/// <summary>
/// Keyboard and mouse handling, ported from <c>dist/input.js</c>.
/// <para>
/// The browser build spread this across three <c>keydown</c> listeners plus mouse and wheel
/// handlers; here it is one class that produces, per frame, the analogue movement vector the
/// engine wants (<see cref="MoveX"/>/<see cref="MoveZ"/>) plus the discrete actions the host
/// applies. Every constant — sensitivity, pitch clamp, the 300 ms double-tap window for
/// creative flight, the <c>(i ± 1 + 9) % 9</c> hotbar wrap — is the JS one.
/// </para>
/// </summary>
public sealed class InputController : IDisposable
{
    /// <summary><c>movementX * .002</c> in input.js.</summary>
    public const float SENSITIVITY = 0.002f;
    public const float PITCH_LIMIT = MathF.PI / 2f;
    public const int SPACE_DOUBLE_TAP_MS = 300;

    private IKeyboard? _keyboard;
    private IMouse? _mouse;
    private Vector2 _lastMouse;
    private bool _haveLastMouse;
    private double _lastSpaceTime = double.NegativeInfinity;
    private double _now;

    /// <summary>
    /// While true, keys feed <see cref="Chars"/>/<see cref="Keys"/> for the text field
    /// instead of the game bindings — the <c>isChatOpen()</c> early-return in input.js.
    /// </summary>
    public bool TextMode;

    /// <summary>Pointer lock, i.e. <c>mouseLook</c>. Mouse look and world clicks need it.</summary>
    public bool PointerLocked;

    public float Yaw, Pitch;

    /// <summary>Normalised world-space movement, matching the <c>move</c> vector in main.js.</summary>
    public double MoveX, MoveZ;
    public bool IsMoving;

    public bool JumpHeld, DescendHeld, Sneaking, Sprinting;
    public bool LeftHeld, RightHeld;
    public bool LeftPressed, RightPressed, LeftReleased;

    public Vector2 MousePosition;
    public float Wheel;
    public bool FlyToggle;

    public List<UiKey> Keys { get; } = new();
    public List<char> Chars { get; } = new();

    /// <summary>Set by the host so the double-tap-space toggle only fires in creative.</summary>
    public Func<bool>? IsCreative;

    public void Attach(IWindow window)
    {
        IInputContext input = window.CreateInput();
        if (input.Keyboards.Count > 0)
        {
            _keyboard = input.Keyboards[0];
            _keyboard.KeyDown += OnKeyDown;
            _keyboard.KeyUp += OnKeyUp;
            _keyboard.KeyChar += OnKeyChar;
        }
        if (input.Mice.Count > 0)
        {
            _mouse = input.Mice[0];
            _mouse.MouseMove += OnMouseMove;
            _mouse.MouseDown += OnMouseDown;
            _mouse.MouseUp += OnMouseUp;
            _mouse.Scroll += OnScroll;
            _lastMouse = _mouse.Position;
            _haveLastMouse = true;
        }
    }

    /// <summary>Advances the frame clock used for the double-tap window.</summary>
    public void BeginFrame(double timeSeconds)
    {
        _now = timeSeconds * 1000.0;
        LeftPressed = RightPressed = LeftReleased = false;
        FlyToggle = false;
        Wheel = 0f;
        Keys.Clear();
        Chars.Clear();
    }

    public void SetCursorLocked(bool locked)
    {
        PointerLocked = locked;
        if (_mouse is null) return;
        _mouse.Cursor.CursorMode = locked ? CursorMode.Disabled : CursorMode.Normal;
        _haveLastMouse = false;   // a mode change jumps the reported position
    }

    // ---------------------------------------------------------------- movement

    /// <summary>
    /// Builds the movement vector main.js computes each frame:
    /// <c>forward = (-sin yaw, 0, -cos yaw)</c>, <c>right = (cos yaw, 0, -sin yaw)</c>,
    /// summed from WASD and normalised.
    /// </summary>
    public void UpdateMovement()
    {
        if (_keyboard is null)
        {
            MoveX = MoveZ = 0;
            IsMoving = false;
            return;
        }

        Sneaking = IsDown(Key.ShiftLeft) || IsDown(Key.ShiftRight);
        Sprinting = (IsDown(Key.ControlLeft) || IsDown(Key.ControlRight)) && !Sneaking;
        JumpHeld = IsDown(Key.Space);
        DescendHeld = Sneaking;

        var forward = new Vector2(-MathF.Sin(Yaw), -MathF.Cos(Yaw));   // (x, z)
        var right = new Vector2(MathF.Cos(Yaw), -MathF.Sin(Yaw));
        var move = Vector2.Zero;
        if (IsDown(Key.W)) move += forward;
        if (IsDown(Key.S)) move -= forward;
        if (IsDown(Key.A)) move -= right;
        if (IsDown(Key.D)) move += right;

        if (move.LengthSquared() > 0f) move = Vector2.Normalize(move);
        MoveX = move.X;
        MoveZ = move.Y;
        IsMoving = move.LengthSquared() > 0f;
    }

    private bool IsDown(Key key)
    {
        if (_keyboard is null) return false;
        try { return _keyboard.IsKeyPressed(key); }
        catch { return false; }
    }

    // -------------------------------------------------------------------- look

    /// <summary>Applies <c>player.yaw -= movementX * .002</c> and the clamped pitch.</summary>
    public void ApplyLook(PlayerEntity player)
    {
        player.Yaw = Yaw;
        player.Pitch = Pitch;
    }

    private void OnMouseMove(IMouse mouse, Vector2 position)
    {
        MousePosition = position;
        if (!PointerLocked || TextMode)
        {
            _lastMouse = position;
            _haveLastMouse = true;
            return;
        }

        Vector2 delta = _haveLastMouse ? position - _lastMouse : Vector2.Zero;
        _lastMouse = position;
        _haveLastMouse = true;

        // Guard against the huge jump some backends emit when the cursor is re-confined.
        if (MathF.Abs(delta.X) > 400f || MathF.Abs(delta.Y) > 400f) return;

        Yaw -= delta.X * SENSITIVITY;
        Pitch -= delta.Y * SENSITIVITY;
        Pitch = Math.Clamp(Pitch, -PITCH_LIMIT, PITCH_LIMIT);
    }

    // ------------------------------------------------------------------ mouse

    private void OnMouseDown(IMouse mouse, MouseButton button)
    {
        MousePosition = mouse.Position;
        if (button == MouseButton.Left) { LeftHeld = true; LeftPressed = true; }
        else if (button == MouseButton.Right) { RightHeld = true; RightPressed = true; }
    }

    private void OnMouseUp(IMouse mouse, MouseButton button)
    {
        if (button == MouseButton.Left) { LeftHeld = false; LeftReleased = true; }
        else if (button == MouseButton.Right) RightHeld = false;
    }

    /// <summary>input.js: <c>idx = (selectedIndex + (deltaY &gt; 0 ? 1 : -1) + 9) % 9</c>.</summary>
    private void OnScroll(IMouse mouse, ScrollWheel wheel)
    {
        float delta = wheel.Y - _lastWheelY;
        _lastWheelY = wheel.Y;
        if (Math.Abs(delta) < 0.01f) return;
        Wheel += delta;
    }

    private float _lastWheelY;

    // ------------------------------------------------------------------- keys

    private void OnKeyChar(IKeyboard keyboard, char c)
    {
        if (!TextMode) return;
        if (c >= ' ' && c <= '~') Chars.Add(c);
    }

    private void OnKeyDown(IKeyboard keyboard, Key key, int scanCode)
    {
        if (TextMode)
        {
            switch (key)
            {
                case Key.Enter: Keys.Add(UiKey.Enter); break;
                case Key.Escape: Keys.Add(UiKey.Escape); break;
                case Key.Backspace: Keys.Add(UiKey.Backspace); break;
                case Key.Delete: Keys.Add(UiKey.Delete); break;
                case Key.Tab: Keys.Add(UiKey.Tab); break;
                case Key.Left: Keys.Add(UiKey.Left); break;
                case Key.Right: Keys.Add(UiKey.Right); break;
                case Key.Home: Keys.Add(UiKey.Home); break;
                case Key.End: Keys.Add(UiKey.End); break;
            }
            return;
        }

        // Space double-tap toggles creative flight (input.js).
        if (key == Key.Space)
        {
            if (IsCreative?.Invoke() ?? false)
            {
                if (_now - _lastSpaceTime < SPACE_DOUBLE_TAP_MS) FlyToggle = true;
                _lastSpaceTime = _now;
            }
        }

        if ((key == Key.S) && (IsDown(Key.ControlLeft) || IsDown(Key.ControlRight)))
            Keys.Add(UiKey.Save);

        switch (key)
        {
            case Key.Escape: Keys.Add(UiKey.Escape); break;
            case Key.E: Keys.Add(UiKey.Inventory); break;
            case Key.U: Keys.Add(UiKey.GamemodeMenu); break;
            case Key.M: Keys.Add(UiKey.Music); break;
            case Key.T: Keys.Add(UiKey.Chat); break;
            case Key.Slash: Keys.Add(UiKey.ChatSlash); break;
            case Key.N: Keys.Add(UiKey.Reload); break;
            case Key.Enter: Keys.Add(UiKey.Enter); break;
            case Key.Tab: Keys.Add(UiKey.Tab); break;
        }
    }

    private void OnKeyUp(IKeyboard keyboard, Key key, int scanCode) { }

    /// <summary>Hotbar digit pressed this frame (0..8), or -1. Read from the raw key state
    /// so it works even between edge events.</summary>
    public int ReadHotbarDigit()
    {
        if (_keyboard is null || TextMode) return -1;
        for (int i = 0; i < 9; i++)
        {
            if (IsDown(Key.Number1 + i)) return i;
        }
        return -1;
    }

    public void Dispose()
    {
        if (_keyboard is not null)
        {
            _keyboard.KeyDown -= OnKeyDown;
            _keyboard.KeyUp -= OnKeyUp;
            _keyboard.KeyChar -= OnKeyChar;
        }
        if (_mouse is not null)
        {
            _mouse.MouseMove -= OnMouseMove;
            _mouse.MouseDown -= OnMouseDown;
            _mouse.MouseUp -= OnMouseUp;
            _mouse.Scroll -= OnScroll;
        }
        _keyboard = null;
        _mouse = null;
    }
}
