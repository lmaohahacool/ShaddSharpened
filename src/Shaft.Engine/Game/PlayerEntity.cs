using Shaft.Engine.Config;
using Shaft.Engine.Items;
using Shaft.Engine.World;

namespace Shaft.Engine.Game;

public enum Gamemode { Creative, Survival }

/// <summary>
/// Player state, movement and survival systems. Ports the physics block of
/// <c>animate()</c> in <c>dist/main.js</c> plus <c>updateFallDamage</c>
/// (mining.js), <c>updateHunger</c> / <c>damagePlayer</c> (ui.js) and
/// <c>spawnPlayer</c> (main.js).
/// </summary>
/// <remarks>
/// <para>
/// Collision is a faithful port of the JS approach: the player is tested as a
/// single point sampled at the feet and at +1.6 on each axis independently, with
/// the vertical test at +1.8 when rising. This is not a swept AABB — it reproduces
/// the original behaviour including its quirks (the player can clip a corner when
/// moving diagonally into an edge). It is preserved deliberately so that Phase A
/// parity is meaningful; replacing it with a proper AABB sweep is a follow-on
/// change that would alter feel and should be reviewed on its own.
/// </para>
/// </remarks>
public sealed class PlayerEntity
{
    private readonly VoxelWorld _world;

    public double X, Y, Z;
    public double VelX, VelY, VelZ;
    public float Yaw, Pitch;
    public bool OnGround;

    public float Health = 20;
    public float MaxHealth = 20;
    public float Hunger = 20;
    public float MaxHunger = 20;

    public float HungerTimer;
    public float StarveTimer;
    public double? FallStartY;

    public bool Sneaking, Sprinting, Flying;
    public Gamemode Mode = Gamemode.Creative;

    /// <summary>Raised when health reaches zero, so the presentation layer can show
    /// the death screen (the JS did this by toggling a DOM element).</summary>
    public event Action? Died;

    /// <summary>Raised on any health change so the HUD hearts can redraw.</summary>
    public event Action? HealthChanged;
    public event Action? HungerChanged;

    public bool IsDead => Health <= 0;

    public PlayerEntity(VoxelWorld world) { _world = world; }

    /// <summary>Port of <c>spawnPlayer</c>.</summary>
    public void Spawn()
    {
        int h = _world.Generator.GetHeight(0, 0);
        X = 0.5;
        Y = Math.Max(h, GameConfig.SEA_LEVEL) + 1.8 + 5;
        Z = 0.5;
        VelX = VelY = VelZ = 0;
        Yaw = 0; Pitch = 0;
        Health = 20; Hunger = 20;
        HungerTimer = 0; StarveTimer = 0;
        FallStartY = null;
        Sneaking = Sprinting = Flying = false;
        HealthChanged?.Invoke();
        HungerChanged?.Invoke();
    }

    /// <summary>Eye height, which drops while sneaking (main.js: <c>eyeH</c>).</summary>
    public float EyeHeight => Sneaking ? GameConfig.PLAYER_SNEAK_EYE : GameConfig.PLAYER_EYE;

    // ------------------------------------------------------------- movement

    /// <summary>
    /// The per-frame physics + survival step. <paramref name="moveX"/> and
    /// <paramref name="moveZ"/> are the already-normalised wish direction in world
    /// space; the caller derives them from WASD and the yaw, as main.js did.
    /// </summary>
    /// <returns>True if the player is translating horizontally, used for footsteps.</returns>
    public bool Update(float dt, double moveX, double moveZ, bool jumpHeld, bool descendHeld)
    {
        bool isMoving = moveX != 0 || moveZ != 0;

        if (Flying)
        {
            VelX = moveX * GameConfig.FLY_SPEED;
            VelZ = moveZ * GameConfig.FLY_SPEED;
            VelY = 0;
            if (jumpHeld) VelY = GameConfig.FLY_SPEED;
            if (descendHeld) VelY = -GameConfig.FLY_SPEED;
        }
        else
        {
            float speed = GameConfig.WALK_SPEED;
            if (Sneaking) speed = GameConfig.SNEAK_SPEED;
            else if (Sprinting) speed = GameConfig.SPRINT_SPEED;

            VelX = moveX * speed;
            VelZ = moveZ * speed;
            VelY -= GameConfig.GRAVITY * dt;
            if (jumpHeld && OnGround) { VelY = GameConfig.JUMP_VELOCITY; OnGround = false; }
        }

        // --- X axis ---
        double newX = X + VelX * dt;
        if (!_world.IsSolid(newX, Y, Z) && !_world.IsSolid(newX, Y + 1.6, Z)) X = newX;
        else VelX = 0;

        // --- Z axis ---
        double newZ = Z + VelZ * dt;
        if (!_world.IsSolid(X, Y, newZ) && !_world.IsSolid(X, Y + 1.6, newZ)) Z = newZ;
        else VelZ = 0;

        // --- Y axis ---
        double newY = Y + VelY * dt;
        OnGround = false;
        if (Flying)
        {
            Y = newY;
        }
        else if (VelY <= 0)
        {
            if (_world.IsSolid(X, newY, Z))
            {
                Y = Math.Floor(newY) + 1;
                VelY = 0;
                OnGround = true;
            }
            else Y = newY;
        }
        else
        {
            if (_world.IsSolid(X, newY + GameConfig.PLAYER_HEIGHT, Z)) VelY = 0;
            else Y = newY;
        }

        UpdateFallDamage(dt);
        UpdateHunger(dt);

        return isMoving;
    }

    /// <summary>Port of <c>updateFallDamage</c>: damage = floor(fallDistance - 3)
    /// for falls beyond three blocks. Skipped in creative and while flying.</summary>
    public void UpdateFallDamage(float dt)
    {
        if (Mode == Gamemode.Creative) return;
        if (Health <= 0) return;
        if (Flying) return;

        if (!OnGround)
        {
            if (FallStartY is null) FallStartY = Y;
            else if (Y > FallStartY.Value) FallStartY = Y;
        }
        else
        {
            if (FallStartY is not null)
            {
                double fd = FallStartY.Value - Y;
                if (fd > 3)
                {
                    int dmg = (int)Math.Floor(fd - 3);
                    Damage(dmg);
                }
                FallStartY = null;
            }
        }
    }

    /// <summary>Port of <c>updateHunger</c> from ui.js: one point per 8 s, then one
    /// health per 2 s once empty.</summary>
    public void UpdateHunger(float dt)
    {
        if (Mode != Gamemode.Survival) return;
        if (Health <= 0) return;

        HungerTimer += dt;
        if (HungerTimer >= 8f)
        {
            HungerTimer = 0;
            if (Hunger > 0)
            {
                Hunger = Math.Max(0, Hunger - 1);
                HungerChanged?.Invoke();
            }
        }

        if (Hunger <= 0)
        {
            StarveTimer += dt;
            if (StarveTimer >= 2f)
            {
                StarveTimer = 0;
                Damage(1);
            }
        }
        else StarveTimer = 0;
    }

    /// <summary>Port of <c>damagePlayer</c>: no-op in creative or when already dead.</summary>
    public void Damage(float amount)
    {
        if (Mode == Gamemode.Creative) return;
        if (Health <= 0) return;

        Health -= amount;
        if (Health < 0) Health = 0;
        HealthChanged?.Invoke();
        if (Health <= 0) Died?.Invoke();
    }

    public void Heal(float amount)
    {
        Health = Math.Min(MaxHealth, Health + amount);
        HealthChanged?.Invoke();
    }

    /// <summary>Port of the right-click-to-eat path in input.js.</summary>
    /// <returns>The food value consumed, or 0 if nothing was eaten.</returns>
    public int Eat(Inventory inventory)
    {
        if (Mode == Gamemode.Creative) return 0;
        int held = inventory.GetSelectedItemId();
        if (held == 0 || !ItemId.IsItem(held)) return 0;
        if (!ItemTable.TryGet(held, out var info) || info.Food <= 0) return 0;
        if (Hunger >= MaxHunger) return 0;

        Hunger = Math.Min(MaxHunger, Hunger + info.Food);
        HungerChanged?.Invoke();
        inventory.Remove(held, 1);
        return info.Food;
    }

    /// <summary>Port of the double-tap-space flight toggle for creative (input.js).</summary>
    public void ToggleFlying()
    {
        if (Mode != Gamemode.Creative) return;
        Flying = !Flying;
        VelY = 0;
    }

    /// <summary>Respawn after death, mirroring the death screen's Respawn button.</summary>
    public void Respawn()
    {
        Spawn();
    }
}
