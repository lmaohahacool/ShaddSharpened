using System.Numerics;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using Shaft.Engine.Config;
using Shaft.Engine.Game;
using Shaft.Engine.Items;
using Shaft.Engine.Meshing;
using Shaft.Engine.World;
using Shaft.Game.Audio;
using Shaft.Game.Input;
using Shaft.Game.Rendering;

namespace Shaft.Game;

/// <summary>
/// The orchestrator: the C# stand-in for the browser's <c>main.js</c> + <c>input.js</c> +
/// <c>ui.js</c> + <c>commands.js</c> + <c>sleeping.js</c> glue.
/// <para>
/// Everything that was DOM in the browser build lives here as plain state. One frame is
/// <see cref="Simulate"/> (input, then the engine, in main.js's order) followed by
/// <see cref="Draw"/> (3D scene, then the immediate-mode HUD). The two never share a click:
/// while the pointer is locked the mouse belongs to the world, and while it is free the mouse
/// belongs to the HUD, which is exactly how <c>mouseLook</c> gated the browser handlers.
/// </para>
/// <para>
/// Behaviours reproduced deliberately, quirks included: the five systems that keep running
/// while a panel is open (day/night, particles, furnaces, water, dropped items) versus the
/// full simulation that stops; <c>processChunkQueues</c> pausing with them; a furnace handing
/// its contents back to the player when its UI closes; switching gamemode wiping the
/// inventory; the sleep camera looking up at −1.2 rad; and <c>trySpawnCows(0, 0)</c> five
/// times on world start, which is what main.js literally does.
/// </para>
/// </summary>
public sealed class GameHost : IDisposable
{
    /// <summary>main.js: <c>dt = Math.min(dt, .05)</c>.</summary>
    private const float MAX_DELTA = 0.05f;

    /// <summary>sleeping.js <c>SLEEP_DURATION</c>.</summary>
    private const float SLEEP_DURATION_MS = 3000f;

    /// <summary>sleeping.js: <c>player.pitch = -1.2</c> and <c>player.pos.y -= 0.3</c>.</summary>
    private const float SLEEP_PITCH = -1.2f;
    private const double SLEEP_DROP = 0.3;

    private const float SAVED_FLASH_SECONDS = 1.0f;    // Ctrl+S "💾 Saved!" timeout
    private const float EAT_FLASH_SECONDS = 0.3f;      // .eating-flash removed after 300 ms
    private const float EAT_POPUP_SECONDS = 0.8f;      // .eat-popup removed after 800 ms
    private const int MAX_PROMPT_CHARS = 32;
    private const int MAX_CHAT_CHARS = 120;

    /// <summary>commands.js <c>Object.keys(COMMANDS)</c>, for Tab completion.</summary>
    private static readonly string[] CommandNames = { "time", "locate", "tp", "help" };

    private readonly IWindow _window;
    private readonly GameOptions _opts;
    private readonly List<string> _warnings = new();
    private readonly InputController _input = new();
    private readonly HudState _st = new();
    private readonly HudEvents _ev = new();
    private readonly Camera _camera = new();
    private readonly SaveSystem _saves;
    private readonly Random _rng = new();

    private GL _gl = null!;
    private TextureSet _textures = null!;
    private HudRenderer? _hud;
    private SceneRenderer? _scene;
    private AudioSystem? _audio;
    private GameSession? _session;

    private int _winW = 1, _winH = 1, _fbW = 1, _fbH = 1;
    private double _lastTime;
    private float _heldBob, _footstepTimer, _sleepTimerMs;
    private float _savedPitch;
    private double _savedY;
    private BlockHit? _target;
    private bool _highlightVisible;
    private bool _musicStarted;
    private bool _loaded;

    public GameHost(IWindow window, GameOptions opts)
    {
        _window = window;
        _opts = opts;
        _saves = new SaveSystem(AssetPaths.SaveRoot);
        _st.BlendValue = Math.Clamp(opts.BiomeBlend, 0, 16);

        window.Load += OnLoad;
        window.Render += OnRender;
        window.Resize += OnResize;
        window.FramebufferResize += OnFramebufferResize;
        window.FocusChanged += OnFocusChanged;
        window.Closing += OnClosing;
    }

    /// <summary>Non-fatal problems worth logging (missing textures, no audio device, …).</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    public GameSession? Session => _session;
    public HudState State => _st;

    // ------------------------------------------------------------------ startup

    private void OnLoad()
    {
        _gl = _window.CreateOpenGL();
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Lequal);
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(TriangleFace.Back);
        _gl.FrontFace(FrontFaceDirection.Ccw);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        // scene.background = new THREE.Color(0x87ceeb)
        _gl.ClearColor(0x87 / 255f, 0xce / 255f, 0xeb / 255f, 1f);

        _winW = Math.Max(1, _window.Size.X);
        _winH = Math.Max(1, _window.Size.Y);
        _fbW = _window.FramebufferSize.X > 0 ? _window.FramebufferSize.X : _winW;
        _fbH = _window.FramebufferSize.Y > 0 ? _window.FramebufferSize.Y : _winH;

        _textures = new TextureSet(_gl, AssetPaths.DistRoot, _warnings);
        _hud = new HudRenderer(_gl, _textures);

        _input.Attach(_window);
        _input.IsCreative = () => _session is { Started: true } && _session.Player.Mode == Gamemode.Creative;

        if (!_opts.DisableAudio)
        {
            _audio = new AudioSystem(AssetPaths.DistRoot);
            foreach (string p in _audio.Problems) _warnings.Add(p);
            _st.MusicEnabled = _audio.MusicEnabled;
        }

        _lastTime = _window.Time;
        _loaded = true;

        if (_opts.SkipTitleScreen)
        {
            int slot = _opts.InitialSlot ?? _saves.NextFreeSlot();
            if (slot >= 0)
            {
                int seed = _opts.InitialSeed ?? _rng.Next(0, 1_000_000);
                if (_opts.InitialSlot is null)
                {
                    string name = string.IsNullOrEmpty(_opts.InitialSaveName) ? "New World" : _opts.InitialSaveName!;
                    _saves.UpdateMetaForSlot(slot, e =>
                    {
                        e.Name = name;
                        e.Seed = seed;
                        e.LastPlayed = NowMs();
                        e.PlayedSeconds = 0;
                    });
                    StartSession(seed, slot, load: false);
                }
                else StartSession(seed, slot, load: true);
            }
        }
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // -------------------------------------------------------------------- frame

    private void OnRender(double deltaSeconds)
    {
        if (!_loaded || _hud is null) return;

        double now = _window.Time;
        float dt = (float)Math.Min(Math.Max(now - _lastTime, 0.0), MAX_DELTA);
        _lastTime = now;

        _audio?.Update();

        Simulate(dt);
        Draw(dt);

        // The edge-triggered input (clicks, key actions, wheel) has now been consumed by
        // both the simulation and the HUD; clear it for the next round of window events.
        _input.BeginFrame(now);
    }

    private void Simulate(float dt)
    {
        GameSession? s = _session;

        _st.PointerLocked = _input.PointerLocked;
        _input.TextMode = _st.ChatOpen || _st.Prompt == PromptKind.NewWorld;

        // ---- title screen: nothing in the world exists yet ----
        if (_st.TitleVisible || s is null || !s.Started)
        {
            HandleTextEntry(null);
            _input.UpdateMovement();
            return;
        }

        HandleTextEntry(s);
        HandleUiKeys(s);

        // sleeping.js updateSleeping(): auto-wake after three seconds.
        if (s.Sleeping)
        {
            _sleepTimerMs += dt * 1000f;
            if (_sleepTimerMs > SLEEP_DURATION_MS) WakeUp(s);
            return;
        }

        bool frozen = _st.AnyPanelOpen(s) || _st.Prompt != PromptKind.None || _st.ChatOpen;

        // Mouse look (input.js mousemove). Frozen while asleep so the −1.2 rad ceiling
        // view survives; sleeping.js has no mousemove guard, but this is what it means.
        if (!frozen) _input.ApplyLook(s.Player);
        _input.UpdateMovement();

        HandleWorldClicks(s, frozen);

        if (frozen)
        {
            // main.js runs these five before its anyPanelOpen() early-return, so the sun
            // keeps moving, furnaces keep smelting and drops keep falling behind a menu.
            s.DayNight.Update(dt);
            _scene?.Particles.Update(dt);
            s.Furnaces.Update(dt);
            s.Water.Update(dt, s.Player.X, s.Player.Z);
            s.Mining.UpdateDroppedItems(dt);
            return;
        }

        // main.js: player.sneaking / player.sprinting, computed before the move vector.
        s.Player.Sneaking = _input.Sneaking;
        s.Player.Sprinting = _input.Sprinting && !_input.Sneaking && !s.Player.Flying;
        if (_input.FlyToggle) s.Player.ToggleFlying();

        bool moving = s.Update(dt, _input.MoveX, _input.MoveZ, _input.JumpHeld, _input.DescendHeld);
        _scene?.Particles.Update(dt);

        // main.js footstep timer: .32 s sprinting, .52 s walking, only on the ground.
        if (moving && s.Player.OnGround && !s.Player.Flying)
        {
            float interval = s.Player.Sprinting ? 0.32f : 0.52f;
            _footstepTimer -= dt;
            if (_footstepTimer <= 0f)
            {
                _footstepTimer = interval;
                _audio?.Play("footstep");
            }
        }
        else _footstepTimer = 0f;

        if (_scene is not null) s.ProcessMeshQueue(_opts.MeshBudgetPerFrame);
    }

    private void Draw(float dt)
    {
        HudRenderer hud = _hud!;
        GameSession? s = _session;

        hud.BeginFrame(_winW, _winH, _fbW, _fbH);
        SyncHudInput(hud);

        if (s is { Started: true } && _scene is not null)
        {
            UpdateCamera(s);

            bool frozen = _st.AnyPanelOpen(s) || _st.Prompt != PromptKind.None || _st.ChatOpen;
            if (!s.Sleeping && !frozen)
            {
                // main.js re-aims the highlight box every frame it does not early-return.
                _target = s.Mining.GetTargetedBlock();
                _highlightVisible = _target is not null;
            }

            _scene.SetSizes(_winW, _winH, _fbW, _fbH);
            _scene.RenderScene(s, _camera, _highlightVisible ? _target : null, _highlightVisible);

            // The held-item pass is inside the same early return in main.js, so it is
            // skipped while a panel is open or the player is asleep.
            if (!s.Sleeping && !frozen && !s.Inventory.Slots[s.Inventory.SelectedIndex].IsEmpty)
            {
                bool moving = _input.IsMoving;
                _heldBob += dt * (moving ? (s.Player.Sprinting ? 14f : 9f) : 2f);
                _scene.RenderHeldItem(s, _heldBob, moving);
            }
        }
        else
        {
            _gl.Viewport(0, 0, (uint)_fbW, (uint)_fbH);
            _gl.Scissor(0, 0, (uint)_fbW, (uint)_fbH);
            _gl.Disable(EnableCap.ScissorTest);
            _gl.ClearColor(0x87 / 255f, 0xce / 255f, 0xeb / 255f, 1f);
            _gl.ClearDepth(1.0);
            _gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));
        }

        HudScreens.Draw(hud, _st, s, _saves, _ev, dt);
        hud.EndFrame();
        ApplyHudEvents();
    }

    /// <summary>
    /// Feeds the HUD from the input controller. While the pointer is locked there is no
    /// visible cursor, so the HUD is handed an off-screen position and no clicks: the
    /// browser build could not click a DOM widget in that state either.
    /// </summary>
    private void SyncHudInput(HudRenderer hud)
    {
        bool ui = !_input.PointerLocked;
        hud.Mouse = ui ? _input.MousePosition : new Vector2(-1f, -1f);
        hud.LeftDown = ui && _input.LeftHeld;
        hud.RightDown = ui && _input.RightHeld;
        hud.MiddleDown = false;
        hud.LeftClicked = ui && _input.LeftPressed;
        hud.RightClicked = ui && _input.RightPressed;
        hud.MiddleClicked = false;
        hud.WheelDelta = _input.Wheel;
        hud.ShiftHeld = _input.Sneaking;
        hud.ControlHeld = _input.Sprinting;
    }

    private void UpdateCamera(GameSession s)
    {
        var p = s.Player;
        // main.js uses 1.6 while sleeping and the sneak-aware eye height otherwise.
        float eye = s.Sleeping ? GameConfig.PLAYER_EYE : p.EyeHeight;
        _camera.Position = new Vector3((float)p.X, (float)(p.Y + eye), (float)p.Z);
        _camera.Yaw = p.Yaw;
        _camera.Pitch = p.Pitch;
        _camera.FovDegrees = 75f;
        _camera.Update(_winH > 0 ? _winW / (float)_winH : 1f);
    }

    // ------------------------------------------------------------------- clicks

    /// <summary>input.js <c>mousedown</c>: only with pointer lock, no panel, no chat, alive.</summary>
    private void HandleWorldClicks(GameSession s, bool frozen)
    {
        if (frozen || !_input.PointerLocked || s.Player.IsDead) return;

        if (_input.LeftPressed)
        {
            // A targeted mob is attacked instead of the block behind it.
            if (s.Mobs.AttackTargeted(s.Inventory))
            {
                _audio?.Play("hit");
                return;
            }
            s.Mining.StartBreaking(s.Mining.GetTargetedBlock());
        }

        if (_input.RightPressed) RightClick(s);

        if (_input.LeftReleased) s.Mining.StopBreaking();
    }

    /// <summary>The right-click branch chain from input.js, in its exact order.</summary>
    private void RightClick(GameSession s)
    {
        BlockHit? target = s.Mining.GetTargetedBlock();
        if (target is null) return;
        byte b = target.Value.Block;

        if (b == Block.CRAFTING_TABLE)
        {
            s.Crafting.OpenCraftingTable();
            SetPointerLock(false);
            return;
        }

        if (b == Block.BED_FOOT || b == Block.BED_HEAD)
        {
            if (s.DayNight.IsNightTime) BeginSleep(s, target.Value);
            return;
        }

        // Chests, furnaces, doors and block placement.
        if (s.Mining.Interact(target))
        {
            if (s.Chests.Open || s.Furnaces.Open) SetPointerLock(false);
            return;
        }

        // Food. Eat() itself enforces survival + hunger &lt; max, as input.js does.
        int food = s.Player.Eat(s.Inventory);
        if (food > 0)
        {
            _audio?.Play("eating");
            _st.EatingFlash = EAT_FLASH_SECONDS;
            _st.EatPopup = EAT_POPUP_SECONDS;
            _st.EatPopupValue = food;
        }
    }

    // --------------------------------------------------------------------- keys

    private void HandleUiKeys(GameSession s)
    {
        // The game key bindings are dead while text entry owns the keyboard.
        if (_st.ChatOpen || _st.Prompt != PromptKind.None) return;

        int digit = _input.ReadHotbarDigit();
        if (digit >= 0) s.Inventory.SelectedIndex = digit;

        // input.js wheel: requires pointer lock, no panel, no chat.
        if (_input.PointerLocked && _input.Wheel != 0f && !_st.AnyPanelOpen(s))
        {
            int notches = (int)Math.Truncate(_input.Wheel);
            if (notches == 0) notches = _input.Wheel > 0f ? 1 : -1;
            int dir = notches > 0 ? 1 : -1;
            for (int i = 0; i < Math.Abs(notches); i++)
            {
                int idx = s.Inventory.SelectedIndex + dir;
                s.Inventory.SelectedIndex = ((idx % 9) + 9) % 9;
            }
        }

        foreach (UiKey key in _input.Keys)
        {
            switch (key)
            {
                case UiKey.Escape:
                    OnEscape(s);
                    break;

                case UiKey.Inventory:                                   // E
                    if (_st.AnyPanelOpen(s)) CloseAllPanels(s);
                    else { s.Crafting.OpenInventory(); SetPointerLock(false); }
                    break;

                case UiKey.GamemodeMenu:                                // U
                    CloseAllPanels(s);
                    _st.GamemodeMenuVisible = true;
                    SetPointerLock(false);
                    break;

                case UiKey.Music:                                       // M
                    if (_audio is not null) _st.MusicEnabled = _audio.ToggleMusic();
                    else _st.MusicEnabled = !_st.MusicEnabled;
                    break;

                case UiKey.Chat:                                        // T
                    OpenChat("");
                    break;

                case UiKey.ChatSlash:                                   // /
                    OpenChat("/");
                    break;

                case UiKey.Save:                                        // Ctrl+S
                    SaveNow(s);
                    break;

                case UiKey.Reload:                                      // N
                    ReturnToTitle(save: true);
                    break;
            }
        }
    }

    /// <summary>input.js Escape: settings first, then any panel, then release the pointer.</summary>
    private void OnEscape(GameSession s)
    {
        if (_st.SettingsVisible)
        {
            _st.SettingsVisible = false;
            _st.ClickHintVisible = true;
            return;
        }
        if (_st.AnyPanelOpen(s))
        {
            CloseAllPanels(s);
            return;
        }
        SetPointerLock(false);
    }

    /// <summary>
    /// input.js <c>closeAllPanels()</c>: furnace, then chest, then inventory. Closing a
    /// furnace returns its three stacks to the player (furnace.js:44); closing the
    /// inventory returns the cursor stack and then the crafting grid.
    /// </summary>
    private void CloseAllPanels(GameSession s)
    {
        if (s.Furnaces.Open)
            foreach (ItemStack it in s.Furnaces.CloseAndCollect())
                s.Inventory.Add(it.Id, it.Count, it.Durability);

        if (s.Chests.Open) s.Chests.CloseChest();

        if (s.Crafting.Open || s.Crafting.TableOpen)
        {
            s.Inventory.ReturnCursor();
            foreach (ItemStack it in s.Crafting.CloseAndCollect())
                s.Inventory.Add(it.Id, it.Count, it.Durability);
        }

        SetPointerLock(true);
    }

    private void SaveNow(GameSession s)
    {
        if (!s.Started || s.CurrentSlot is null) return;
        try
        {
            s.Save();
            _st.SavedFlash = SAVED_FLASH_SECONDS;
        }
        catch (Exception ex)
        {
            _warnings.Add($"save failed: {ex.Message}");
        }
    }

    // --------------------------------------------------------------------- chat

    private void OpenChat(string initial)
    {
        _st.ChatOpen = true;
        _st.ChatBuffer = initial;
        _st.Caret = initial.Length;
        SetPointerLock(false);
    }

    private void CloseChat()
    {
        _st.ChatOpen = false;
        _st.ChatBuffer = "";
        _st.Caret = 0;
        GameSession? s = _session;
        if (s is { Started: true } && !_st.AnyPanelOpen(s) && _st.Prompt == PromptKind.None)
            SetPointerLock(true);
    }

    private void SubmitChat(GameSession? s)
    {
        string text = _st.ChatBuffer;
        CloseChat();
        if (s is null || text.Trim().Length == 0) return;

        s.Chat.Add("> " + text);
        string result = s.RunCommand(text);
        if (result.Length == 0) return;
        foreach (string line in result.Split('\n'))
            if (line.Length > 0) s.Chat.Add(line);
    }

    /// <summary>commands.js Tab: one match completes, several are listed as suggestions.</summary>
    private void AutocompleteChat(GameSession? s)
    {
        string buffer = _st.ChatBuffer;
        if (buffer.Length == 0 || buffer.Contains(' ')) return;
        string prefix = buffer.StartsWith('/') ? buffer[1..] : buffer;

        var matches = new List<string>();
        foreach (string name in CommandNames)
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) matches.Add("/" + name);

        if (matches.Count == 1)
        {
            _st.ChatBuffer = matches[0] + " ";
            _st.Caret = _st.ChatBuffer.Length;
        }
        else if (matches.Count > 1)
            s?.Chat.Add("Suggestions: " + string.Join(", ", matches));
    }

    /// <summary>Text editing for the chat line and the new-world prompt.</summary>
    private void HandleTextEntry(GameSession? s)
    {
        if (_st.ChatOpen)
        {
            foreach (char c in _input.Chars)
            {
                if (_st.ChatBuffer.Length >= MAX_CHAT_CHARS) break;
                _st.ChatBuffer = _st.ChatBuffer.Insert(_st.Caret, c.ToString());
                _st.Caret++;
            }

            foreach (UiKey key in _input.Keys)
            {
                switch (key)
                {
                    case UiKey.Enter: SubmitChat(s); break;
                    case UiKey.Escape: CloseChat(); break;
                    case UiKey.Tab: AutocompleteChat(s); break;
                    case UiKey.Backspace:
                        if (_st.Caret > 0)
                        {
                            _st.ChatBuffer = _st.ChatBuffer.Remove(_st.Caret - 1, 1);
                            _st.Caret--;
                        }
                        break;
                    case UiKey.Delete:
                        if (_st.Caret < _st.ChatBuffer.Length)
                            _st.ChatBuffer = _st.ChatBuffer.Remove(_st.Caret, 1);
                        break;
                    case UiKey.Left: _st.Caret = Math.Max(0, _st.Caret - 1); break;
                    case UiKey.Right: _st.Caret = Math.Min(_st.ChatBuffer.Length, _st.Caret + 1); break;
                    case UiKey.Home: _st.Caret = 0; break;
                    case UiKey.End: _st.Caret = _st.ChatBuffer.Length; break;
                }
            }
            return;
        }

        if (_st.Prompt != PromptKind.NewWorld) return;

        // DrawPrompt always renders the caret at the end of the value, so the prompt
        // edits at the end only — window.prompt() had no caret to move either.
        foreach (char c in _input.Chars)
            if (_st.PromptValue.Length < MAX_PROMPT_CHARS) _st.PromptValue += c;

        foreach (UiKey key in _input.Keys)
        {
            switch (key)
            {
                case UiKey.Enter: ConfirmPrompt(_st.PromptValue); break;
                case UiKey.Escape: _st.Prompt = PromptKind.None; break;
                case UiKey.Backspace:
                    if (_st.PromptValue.Length > 0)
                        _st.PromptValue = _st.PromptValue[..^1];
                    break;
            }
        }
    }

    // ------------------------------------------------------------------ sleeping

    private void BeginSleep(GameSession s, BlockHit target)
    {
        if (!s.TrySleep(target)) return;
        _savedPitch = s.Player.Pitch;
        _savedY = s.Player.Y;
        s.Player.Y -= SLEEP_DROP;
        s.Player.Pitch = SLEEP_PITCH;
        _input.Pitch = SLEEP_PITCH;
        _sleepTimerMs = 0f;
        _st.SleepVisible = true;
    }

    private void WakeUp(GameSession s)
    {
        if (!s.Sleeping) return;
        // The engine handles world time + vitals (sleeping.js:wakeUp); the host owns the
        // saved camera pose (window._sleepSaved*) and the sleep overlay.
        s.WakeUp(skipToMorning: true);
        s.Player.Pitch = _savedPitch;
        s.Player.Y = _savedY;
        _input.Pitch = _savedPitch;
        _st.SleepVisible = false;
        _sleepTimerMs = 0f;
    }

    // ------------------------------------------------------------- pointer lock

    /// <summary>
    /// requestPointerLock()/exitPointerLock() plus the <c>pointerlockchange</c> handler,
    /// which is what decides whether the click-to-play hint or the crosshair is showing.
    /// </summary>
    private void SetPointerLock(bool locked)
    {
        _input.SetCursorLocked(locked);
        _st.PointerLocked = locked;

        if (locked)
        {
            _st.ClickHintVisible = false;
            // main.js starts the music on the first click that locks the pointer.
            if (!_musicStarted)
            {
                _audio?.StartMusic();
                _musicStarted = true;
            }
            return;
        }

        _session?.Mining.StopBreaking();
        GameSession? s = _session;
        _st.ClickHintVisible = s is { Started: true } && !_st.AnyPanelOpen(s) && !_st.ChatOpen;
    }

    private void SyncLook(GameSession s)
    {
        _input.Yaw = s.Player.Yaw;
        _input.Pitch = s.Player.Pitch;
    }

    // -------------------------------------------------------------- world start

    /// <summary>main.js <c>startGame(slot, seed, pendingSave)</c>.</summary>
    private void StartSession(int seed, int slot, bool load)
    {
        try
        {
            TeardownSession(save: false);

            var session = new GameSession(seed, AssetPaths.SaveRoot, _st.BlendValue, _opts.SpawnBiome);
            session.EnableAsyncStreaming();
            _session = session;

            session.ChunkMeshed += OnChunkMeshed;
            session.ChunkUnloaded += OnChunkUnloaded;
            session.Mining.BlockBroken += OnBlockBroken;
            session.Mining.SoundRequested += OnSoundRequested;
            session.Player.Died += OnPlayerDied;

            _scene?.Dispose();
            _scene = new SceneRenderer(_gl, _textures, _opts, session.World, _warnings);
            _scene.SetSizes(_winW, _winH, _fbW, _fbH);

            if (load) session.LoadGame(slot);
            // core.js starts every page load in creative; a new world keeps that default.
            else session.StartNewGame(slot, Gamemode.Creative);

            // main.js: for(let i = 0; i &lt; 5; i++) trySpawnCows(0, 0);
            for (int i = 0; i < 5; i++) session.Mobs.TrySpawnCows(0, 0);

            SyncLook(session);
            _st.TitleVisible = false;
            _st.GameStarted = true;
            _st.DeathVisible = false;
            _st.SleepVisible = false;
            _st.TitleStatus = "";
            _st.ChatOpen = false;
            _st.ChatBuffer = "";
            _heldBob = 0f;
            _footstepTimer = 0f;
            _sleepTimerMs = 0f;
            _target = null;
            _highlightVisible = false;

            SetPointerLock(false);      // shows the "click to play" hint
        }
        catch (Exception ex)
        {
            _warnings.Add($"could not start world: {ex.Message}");
            _st.TitleStatus = ex.Message;
            _st.TitleVisible = true;
            _st.GameStarted = false;
            TeardownSession(save: false);
        }
    }

    private void TeardownSession(bool save)
    {
        GameSession? s = _session;
        if (s is not null)
        {
            if (save)
            {
                try { s.Save(); }
                catch (Exception ex) { _warnings.Add($"save failed: {ex.Message}"); }
            }

            s.ChunkMeshed -= OnChunkMeshed;
            s.ChunkUnloaded -= OnChunkUnloaded;
            s.Mining.BlockBroken -= OnBlockBroken;
            s.Mining.SoundRequested -= OnSoundRequested;
            s.Player.Died -= OnPlayerDied;
            s.Dispose();
        }

        _session = null;
        _scene?.ClearChunks();
    }

    /// <summary>The N key and "Back to title": a browser reload saved on <c>beforeunload</c>.</summary>
    private void ReturnToTitle(bool save)
    {
        TeardownSession(save);
        _scene?.Dispose();
        _scene = null;

        _st.TitleVisible = true;
        _st.GameStarted = false;
        _st.DeathVisible = false;
        _st.SleepVisible = false;
        _st.SettingsVisible = false;
        _st.GamemodeMenuVisible = false;
        _st.ChatOpen = false;
        _st.ChatBuffer = "";
        _st.Caret = 0;
        _st.Prompt = PromptKind.None;
        _st.ClickHintVisible = false;
        _st.TitleStatus = "";
        _target = null;
        _highlightVisible = false;
        SetPointerLock(false);
    }

    private void OnPlayerDied()
    {
        _st.DeathVisible = true;
        SetPointerLock(false);
    }

    // ------------------------------------------------------------ HUD callbacks

    private void OnChunkMeshed(Chunk chunk, ChunkMeshData data)
        => _scene?.UploadChunk(ChunkKeys.Pack(chunk.CX, chunk.CZ), chunk, data);

    private void OnChunkUnloaded(long key) => _scene?.DropChunk(key);

    private void OnBlockBroken(int x, int y, int z, byte block)
        => _scene?.Particles.SpawnBreak(x, y, z, block);

    private void OnSoundRequested(string key) => _audio?.Play(key);

    private void ApplyHudEvents()
    {
        HudEvents ev = _ev;
        GameSession? s = _session;

        if (ev.SelectHotbar is int hotbar && s is not null)
            s.Inventory.SelectedIndex = Math.Clamp(hotbar, 0, Inventory.HOTBAR - 1);

        // ---- inventory / containers ----
        if (ev.InvClick is (int slot, bool right, bool shift) && s is not null)
            s.Inventory.ClickSlot(slot, right, shift);

        if (ev.CraftClick is (int grid, bool gRight, bool gShift) && s is not null)
            s.Crafting.ClickGridSlot(grid, gRight, gShift, s.Inventory);

        if (ev.CraftOutputClick is (bool outRight, _) && s is not null)
        {
            // crafting.js: the output slot is inert while the cursor holds something.
            if (s.Inventory.Cursor.IsEmpty)
            {
                ItemStack? made = s.Crafting.TakeOutput(single: outRight);
                if (made is ItemStack stack && !stack.IsEmpty) s.Inventory.Cursor = stack;
            }
        }

        if (ev.CreativeGive is (int id, int amount) && s is not null && s.Player.Mode == Gamemode.Creative)
            s.Inventory.Add(id, amount);

        if (ev.TrashClick && s is not null) s.Inventory.Cursor = ItemStack.Empty;

        if (ev.ChestClick is (int chestSlot, bool cRight, bool cShift) &&
            s is not null && s.Chests.OpenKey is BlockPos chestKey)
        {
            ItemStack[] chest = s.Chests.GetState(chestKey.X, chestKey.Y, chestKey.Z);
            if (chestSlot >= 0 && chestSlot < chest.Length)
                s.Inventory.ClickExternalSlot(ref chest[chestSlot], cRight, cShift);
        }

        if (ev.FurnaceClick is (int furnaceSlot, bool fRight, bool fShift) &&
            s is not null && s.Furnaces.OpenKey is BlockPos furnaceKey)
        {
            FurnaceState state = s.Furnaces.GetState(furnaceKey.X, furnaceKey.Y, furnaceKey.Z);
            if (furnaceSlot == 0) s.Inventory.ClickExternalSlot(ref state.Input, fRight, fShift);
            else if (furnaceSlot == 1) s.Inventory.ClickExternalSlot(ref state.Fuel, fRight, fShift);
            else s.Inventory.ClickExternalSlot(ref state.Output, fRight, fShift);
        }

        // ---- menus ----
        if (ev.SetCreative && s is not null)
        {
            s.SetGamemode(Gamemode.Creative);
            _st.GamemodeMenuVisible = false;
            SetPointerLock(true);
        }
        if (ev.SetSurvival && s is not null)
        {
            s.SetGamemode(Gamemode.Survival);
            _st.GamemodeMenuVisible = false;
            SetPointerLock(true);
        }
        if (ev.CloseGamemodeMenu)
        {
            _st.GamemodeMenuVisible = false;
            SetPointerLock(true);
        }
        if (ev.CloseSettings)
        {
            // btn-close-settings hides the menu and brings the click hint back.
            _st.SettingsVisible = false;
            _st.ClickHintVisible = true;
        }
        if (ev.SaveAndQuit)
        {
            _st.Prompt = PromptKind.SaveQuit;
            _st.PromptMessage = "Save and quit to menu? Your progress will be saved.";
        }
        if (ev.SetBlend is int blend)
        {
            // main.js: setBiomeBlend(v); rebuildAllChunks();
            _opts.BiomeBlend = blend;
            _st.BlendValue = blend;
            s?.World.SetBiomeBlend(blend);
        }
        if (ev.WakeUp && s is not null) WakeUp(s);
        if (ev.Respawn && s is not null)
        {
            s.Player.Respawn();
            _st.DeathVisible = false;
            SyncLook(s);
            SetPointerLock(true);
        }
        if (ev.BackToTitle) ReturnToTitle(save: true);

        if (ev.RequestPointerLock) SetPointerLock(true);
        if (ev.OpenSettingsFromHint)
        {
            _st.ClickHintVisible = false;
            _st.SettingsVisible = true;
        }

        // ---- title screen ----
        if (ev.NewWorld)
        {
            _st.Prompt = PromptKind.NewWorld;
            _st.PromptMessage = "World name:";
            _st.PromptValue = "New World";
        }
        if (ev.PlaySaveSlot is int playSlot) StartFromSaveSlot(playSlot);
        if (ev.DeleteSaveSlot is int deleteSlot)
        {
            _st.Prompt = PromptKind.DeleteWorld;
            _st.PromptSlot = deleteSlot;
            _st.PromptMessage = "Delete this world?";
        }
        if (ev.PromptConfirm is string confirm) ConfirmPrompt(confirm);
        if (ev.PromptCancel) _st.Prompt = PromptKind.None;

        ev.Reset();
    }

    /// <summary>main.js <c>startGameFromSlot</c>: the seed comes from the save itself.</summary>
    private void StartFromSaveSlot(int slot)
    {
        int? seed = null;
        SaveMetaEntry? entry = _saves.GetSavesMeta().Saves.Find(e => e.Slot == slot);
        if (entry is not null) seed = entry.Seed;
        else
        {
            var probed = _saves.LoadGame(slot);
            if (probed is null)
            {
                _st.TitleStatus = "Save corrupted or missing.";
                return;
            }
            seed = (int)probed.Seed;
        }

        StartSession(seed.Value, slot, load: true);
    }

    private void ConfirmPrompt(string value)
    {
        switch (_st.Prompt)
        {
            case PromptKind.NewWorld:
            {
                string name = string.IsNullOrWhiteSpace(value) ? "New World" : value.Trim();
                int slot = _saves.NextFreeSlot();
                if (slot < 0)
                {
                    _st.TitleStatus = "No free save slots.";
                    break;
                }
                // main.js: Math.floor(Math.random() * 1000000)
                int seed = _rng.Next(0, 1_000_000);
                _saves.UpdateMetaForSlot(slot, e =>
                {
                    e.Name = name;
                    e.Seed = seed;
                    e.LastPlayed = NowMs();
                    e.PlayedSeconds = 0;
                });
                _st.Prompt = PromptKind.None;
                StartSession(seed, slot, load: false);
                return;
            }
            case PromptKind.DeleteWorld:
                if (_st.PromptSlot is int doomed) _saves.DeleteSlot(doomed);
                _st.TitleStatus = "";
                break;

            case PromptKind.SaveQuit:
                _st.Prompt = PromptKind.None;
                ReturnToTitle(save: true);
                return;
        }

        _st.Prompt = PromptKind.None;
    }

    // ------------------------------------------------------------------- window

    private void OnResize(Silk.NET.Maths.Vector2D<int> size)
    {
        _winW = Math.Max(1, size.X);
        _winH = Math.Max(1, size.Y);
    }

    private void OnFramebufferResize(Silk.NET.Maths.Vector2D<int> size)
    {
        if (size.X > 0) _fbW = size.X;
        if (size.Y > 0) _fbH = size.Y;
    }

    private void OnFocusChanged(bool focused)
    {
        if (focused) return;
        // A lost focus would otherwise leave movement keys stuck and the pointer locked.
        _session?.Mining.StopBreaking();
        if (_session is { Started: true }) SetPointerLock(false);
    }

    private void OnClosing()
    {
        // window.addEventListener('beforeunload', …) saved the world on the way out.
        TeardownSession(save: true);
    }

    public void Dispose()
    {
        _window.Load -= OnLoad;
        _window.Render -= OnRender;
        _window.Resize -= OnResize;
        _window.FramebufferResize -= OnFramebufferResize;
        _window.FocusChanged -= OnFocusChanged;
        _window.Closing -= OnClosing;

        TeardownSession(save: true);
        _scene?.Dispose();
        _scene = null;
        _hud?.Dispose();
        _hud = null;
        _textures?.Dispose();
        _audio?.Dispose();
        _input.Dispose();
        _loaded = false;
    }
}
