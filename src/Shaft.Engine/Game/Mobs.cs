using Shaft.Engine.Config;
using Shaft.Engine.Items;
using Shaft.Engine.World;

namespace Shaft.Engine.Game;

public enum MobType { Cow, Zombie }

/// <summary>
/// A mob entity. Ports the <c>Mob</c> and <c>Zombie</c> classes from
/// <c>dist/mobs.js</c>.
/// </summary>
/// <remarks>
/// <c>mobs.js</c> is 855 lines, most of which builds and animates three.js object
/// hierarchies (limb groups, materials, emissive flash). That presentation code has
/// no C# analogue here and belongs to the renderer, so this class carries the
/// simulation state and behaviour — health, knockback, gravity, wander/chase AI,
/// burning, persistence and spawning — while exposing the fields the renderer needs
/// (<see cref="WalkAnim"/>, <see cref="FlashTimer"/>, <see cref="Yaw"/>). The saved
/// field set matches <c>save.js</c> exactly.
/// </remarks>
public sealed class Mob
{
    public MobType Type;
    public double X, Y, Z;
    public double VelX, VelY, VelZ;
    public float Health;
    public bool OnGround;
    public float WalkAnim;
    public float Yaw;

    /// <summary>Cow-only: seconds until the next wander decision.</summary>
    public float IdleTimer;
    /// <summary>Zombie-only: daylight burning state and its two timers.</summary>
    public bool Burning;
    public float BurnTimer;
    public float BurnCheckTimer = 0.5f;

    public float AttackCooldown;
    public float HurtTimer;
    /// <summary>Presentation hook: drives the red emissive flash on being hit.</summary>
    public float FlashTimer;

    /// <summary>Persistent mobs are saved and return home if they wander too far.</summary>
    public bool Persistent;
    public double HomeX, HomeY, HomeZ;
    public bool HasHome;

    public bool Dead => Health <= 0;

    /// <summary>Body height used for the ceiling test, per mob type.</summary>
    public float Height => Type == MobType.Cow ? 1.2f : 1.8f;

    public Mob(MobType type, double x, double y, double z)
    {
        Type = type;
        X = x; Y = y; Z = z;
        Health = type == MobType.Cow ? 10 : 20;
        // Zombies are always persistent (hostile creatures); cows only once tamed.
        Persistent = type == MobType.Zombie;
        IdleTimer = 3f + (float)Random.Shared.NextDouble() * 4f;
    }

    public void SetPersistent()
    {
        Persistent = true;
        HomeX = X; HomeY = Y; HomeZ = Z;
        HasHome = true;
    }
}

/// <summary>
/// Port of <c>updateMobs</c>, <c>trySpawnCows</c>, <c>trySpawnZombies</c> and the
/// per-mob <c>update</c>/<c>damage</c> methods from <c>dist/mobs.js</c>.
/// </summary>
public sealed class MobSystem
{
    public const int MAX_COWS = 15;
    public const int MAX_ZOMBIES = 8;
    public const float COW_SPAWN_INTERVAL = 10f;
    public const float ZOMBIE_SPAWN_INTERVAL = 3f;
    public const float ZOMBIE_DAMAGE = 3f;
    public const float ZOMBIE_ATTACK_RANGE = 1.2f;
    public const float ZOMBIE_ATTACK_COOLDOWN = 1f;
    public const float BURN_CHECK_INTERVAL = 0.5f;
    public const float BURN_TICK_INTERVAL = 1.5f;

    private readonly VoxelWorld _world;
    private readonly PlayerEntity _player;
    private readonly DayNightSystem _dayNight;
    private readonly MiningSystem? _mining;
    private readonly Random _rng = new();

    public List<Mob> Mobs { get; } = new();

    private float _cowSpawnTimer, _zombieSpawnTimer;

    /// <summary>Raised when a mob dies so the caller can drop loot and play a sound.</summary>
    public event Action<Mob>? MobKilled;

    public MobSystem(VoxelWorld world, PlayerEntity player, DayNightSystem dayNight, MiningSystem? mining = null)
    {
        _world = world; _player = player; _dayNight = dayNight; _mining = mining;
    }

    /// <summary>Port of <c>mobSolid</c>: the block solidity test mobs use.</summary>
    private bool MobSolid(double x, double y, double z)
        => _world.IsSolid((int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z));

    // ----------------------------------------------------------------- update

    /// <summary>Port of <c>updateMobs</c>.</summary>
    public void Update(float dt)
    {
        for (int i = Mobs.Count - 1; i >= 0; i--)
        {
            UpdateMob(Mobs[i], dt);
            if (Mobs[i].Dead)
            {
                var m = Mobs[i];
                Mobs.RemoveAt(i);
                MobKilled?.Invoke(m);
            }
        }

        _cowSpawnTimer += dt;
        if (_cowSpawnTimer > COW_SPAWN_INTERVAL)
        {
            _cowSpawnTimer = 0;
            if (CountType(MobType.Cow) < MAX_COWS) TrySpawnCows(_player.X, _player.Z);
        }

        _zombieSpawnTimer += dt;
        if (_zombieSpawnTimer > ZOMBIE_SPAWN_INTERVAL)
        {
            _zombieSpawnTimer = 0;
            TrySpawnZombies(_player.X, _player.Z);
        }
    }

    public int CountType(MobType t)
    {
        int n = 0;
        foreach (var m in Mobs) if (m.Type == t) n++;
        return n;
    }

    private void UpdateMob(Mob m, float dt)
    {
        if (m.Type == MobType.Zombie && UpdateBurning(m, dt)) return;

        // Shared integration, exactly as in mobs.js.
        m.VelY -= GameConfig.MOB_GRAVITY * dt;
        double damp = Math.Pow(0.15, dt);
        m.VelX *= damp;
        m.VelZ *= damp;
        m.AttackCooldown -= dt;
        if (m.FlashTimer > 0) m.FlashTimer -= dt;
        m.HurtTimer -= dt;
        bool skipAI = m.HurtTimer > 0;

        if (!skipAI) RunAI(m, dt);

        // --- collision, same point-sampling scheme as the player ---
        double kx = m.X + m.VelX * dt;
        double kz = m.Z + m.VelZ * dt;
        if (!MobSolid(kx, m.Y, m.Z) && !MobSolid(kx, m.Y + m.Height * 0.9, m.Z)) m.X = kx;
        else m.VelX = 0;
        if (!MobSolid(m.X, m.Y, kz) && !MobSolid(m.X, m.Y + m.Height * 0.9, kz)) m.Z = kz;
        else m.VelZ = 0;

        double ny = m.Y + m.VelY * dt;
        m.OnGround = false;
        if (m.VelY <= 0)
        {
            if (MobSolid(m.X, ny, m.Z)) { m.Y = Math.Floor(ny) + 1; m.VelY = 0; m.OnGround = true; }
            else m.Y = ny;
        }
        else
        {
            if (MobSolid(m.X, ny + m.Height, m.Z)) m.VelY = 0;
            else m.Y = ny;
        }

        double speed = Math.Sqrt(m.VelX * m.VelX + m.VelZ * m.VelZ);
        if (speed > 0.05) m.WalkAnim += dt * (float)speed * 6f;
    }

    private void RunAI(Mob m, float dt)
    {
        double dx = _player.X - m.X, dz = _player.Z - m.Z;
        double dist = Math.Sqrt(dx * dx + dz * dz);

        if (m.Type == MobType.Zombie)
        {
            // Chase the player in survival; burn-away otherwise handled separately.
            if (_player.Mode == Gamemode.Survival && dist < 24 && !_player.IsDead)
            {
                const float chaseSpeed = 2.6f;
                m.VelX += dx / Math.Max(dist, 0.001) * chaseSpeed * dt * 8;
                m.VelZ += dz / Math.Max(dist, 0.001) * chaseSpeed * dt * 8;
                m.Yaw = (float)Math.Atan2(dx, dz);

                if (dist < ZOMBIE_ATTACK_RANGE && m.AttackCooldown <= 0
                    && Math.Abs(m.Y - _player.Y) < 1.5)
                {
                    _player.Damage(ZOMBIE_DAMAGE);
                    m.AttackCooldown = ZOMBIE_ATTACK_COOLDOWN;
                }
            }
            else
            {
                Wander(m, dt);
            }
            return;
        }

        // Cow: a persistent cow that strayed too far heads home.
        if (m.Persistent && m.HasHome)
        {
            double hx = m.HomeX - m.X, hz = m.HomeZ - m.Z;
            double hd = Math.Sqrt(hx * hx + hz * hz);
            if (hd > 24)
            {
                m.VelX += hx / hd * 2.0 * dt * 8;
                m.VelZ += hz / hd * 2.0 * dt * 8;
                m.Yaw = (float)Math.Atan2(hx, hz);
                return;
            }
        }
        Wander(m, dt);
    }

    /// <summary>Idle wander: pick a new heading every 3-7 seconds.</summary>
    private void Wander(Mob m, float dt)
    {
        m.IdleTimer -= dt;
        if (m.IdleTimer <= 0)
        {
            m.IdleTimer = 3f + (float)_rng.NextDouble() * 4f;
            if (_rng.NextDouble() < 0.55)
            {
                double a = _rng.NextDouble() * Math.PI * 2;
                m.VelX += Math.Cos(a) * 1.6;
                m.VelZ += Math.Sin(a) * 1.6;
                m.Yaw = (float)Math.Atan2(Math.Cos(a), Math.Sin(a));
            }
        }
    }

    /// <summary>Port of <c>Zombie.updateBurning</c>: catches fire in daylight with an
    /// unobstructed column to the sky, taking 1 damage every 1.5 s.</summary>
    private bool UpdateBurning(Mob m, float dt)
    {
        m.BurnCheckTimer -= dt;
        if (m.BurnCheckTimer <= 0)
        {
            m.BurnCheckTimer = BURN_CHECK_INTERVAL;
            m.Burning = !_dayNight.IsNightTime && HasSkyAbove(m);
        }

        if (!m.Burning) return false;

        m.BurnTimer -= dt;
        if (m.BurnTimer <= 0)
        {
            m.BurnTimer = BURN_TICK_INTERVAL;
            Damage(m, 1, m.X, m.Z);
            return m.Dead;
        }
        return false;
    }

    /// <summary>Port of <c>hasSkyAbove</c>: true when nothing solid blocks the sky.</summary>
    private bool HasSkyAbove(Mob m)
    {
        int x = (int)Math.Floor(m.X), z = (int)Math.Floor(m.Z);
        int y = (int)Math.Floor(m.Y + m.Height);
        for (int checkY = y; checkY < GameConfig.CHUNK_MAX_Y; checkY++)
            if (_world.GetBlock(x, checkY, z) != Block.AIR) return false;
        return true;
    }

    /// <summary>Port of <c>Mob.damage</c>: applies knockback away from the damage source.</summary>
    public void Damage(Mob m, float amount, double sourceX, double sourceZ)
    {
        m.Health -= amount;
        m.HurtTimer = 0.3f;
        m.FlashTimer = 0.15f;

        double dx = m.X - sourceX, dz = m.Z - sourceZ;
        double len = Math.Sqrt(dx * dx + dz * dz);
        if (len > 0.001)
        {
            m.VelX += dx / len * 6;
            m.VelZ += dz / len * 6;
            m.VelY = 4;
        }
    }

    // ---------------------------------------------------------------- spawning

    /// <summary>Port of <c>spawnCow</c> / <c>spawnZombie</c>: placed one block above terrain.</summary>
    public Mob SpawnMob(MobType type, int x, int z)
    {
        int y = _world.Generator.GetHeight(x, z) + 1;
        var m = new Mob(type, x + 0.5, y, z + 0.5);
        Mobs.Add(m);
        return m;
    }

    /// <summary>Port of <c>trySpawnCows</c>: 20 attempts on a 10-40 block ring, grassy
    /// biomes only, above sea level, not within 3 blocks of another mob, 50% each.</summary>
    public void TrySpawnCows(double px, double pz)
    {
        for (int i = 0; i < 20; i++)
        {
            double a = _rng.NextDouble() * Math.PI * 2;
            double d = 10 + _rng.NextDouble() * 30;
            int cx = (int)Math.Floor(px + Math.Cos(a) * d);
            int cz = (int)Math.Floor(pz + Math.Sin(a) * d);

            int biome = _world.Generator.GetBiome(cx, cz);
            if (biome != Biome.PLAINS && biome != Biome.FOREST && biome != Biome.OLD_GROWTH) continue;
            if (_world.Generator.GetHeight(cx, cz) <= GameConfig.SEA_LEVEL) continue;
            if (AnyMobWithin(cx, cz, 3)) continue;
            if (_rng.NextDouble() < 0.5) SpawnMob(MobType.Cow, cx, cz);
        }
    }

    /// <summary>Port of <c>trySpawnZombies</c>: night only, capped at 8, on a 14-34
    /// block ring, not within 2 blocks of another mob, 40% each.</summary>
    public void TrySpawnZombies(double px, double pz)
    {
        if (!_dayNight.IsNightTime) return;
        if (CountType(MobType.Zombie) >= MAX_ZOMBIES) return;

        for (int i = 0; i < 6; i++)
        {
            double a = _rng.NextDouble() * Math.PI * 2;
            double d = 14 + _rng.NextDouble() * 20;
            int cx = (int)Math.Floor(px + Math.Cos(a) * d);
            int cz = (int)Math.Floor(pz + Math.Sin(a) * d);

            if (_world.Generator.GetHeight(cx, cz) <= GameConfig.SEA_LEVEL) continue;
            if (AnyMobWithin(cx, cz, 2)) continue;
            if (_rng.NextDouble() < 0.4) SpawnMob(MobType.Zombie, cx, cz);
        }
    }

    private bool AnyMobWithin(double x, double z, double radius)
    {
        foreach (var m in Mobs)
            if (Math.Abs(m.X - x) < radius && Math.Abs(m.Z - z) < radius) return true;
        return false;
    }

    // ------------------------------------------------------------------ attack

    /// <summary>Port of <c>getTargetedMob</c>: the mob the crosshair is on, within reach.</summary>
    public Mob? GetTargetedMob()
    {
        double ox = _player.X, oy = _player.Y + GameConfig.PLAYER_EYE, oz = _player.Z;
        MiningSystem.LookDirection(_player.Yaw, _player.Pitch, out double dx, out double dy, out double dz);

        Mob? best = null;
        double bestT = double.MaxValue;

        foreach (var m in Mobs)
        {
            // Ray vs. an upright box around the mob.
            double minX = m.X - 0.45, maxX = m.X + 0.45;
            double minY = m.Y, maxY = m.Y + m.Height;
            double minZ = m.Z - 0.45, maxZ = m.Z + 0.45;

            double tmin = 0, tmax = MiningSystem.REACH;
            if (!Slab(ox, dx, minX, maxX, ref tmin, ref tmax)) continue;
            if (!Slab(oy, dy, minY, maxY, ref tmin, ref tmax)) continue;
            if (!Slab(oz, dz, minZ, maxZ, ref tmin, ref tmax)) continue;
            if (tmin < bestT) { bestT = tmin; best = m; }
        }
        return best;
    }

    private static bool Slab(double o, double d, double lo, double hi, ref double tmin, ref double tmax)
    {
        if (Math.Abs(d) < 1e-8) return o >= lo && o <= hi;
        double t1 = (lo - o) / d, t2 = (hi - o) / d;
        if (t1 > t2) (t1, t2) = (t2, t1);
        tmin = Math.Max(tmin, t1);
        tmax = Math.Min(tmax, t2);
        return tmin <= tmax;
    }

    /// <summary>Left-click attack: damages the targeted mob and, in survival, wears the tool.</summary>
    public bool AttackTargeted(Inventory inventory)
    {
        var m = GetTargetedMob();
        if (m is null) return false;

        int held = inventory.GetSelectedItemId();
        float dmg = 1f;
        if (held != 0 && ItemId.IsItem(held) && ItemTable.TryGet(held, out var info))
        {
            if (info.Tool == ToolKind.Pickaxe) dmg = info.Durability >= 130 ? 4f : 3f;
            else if (info.Tool == ToolKind.Axe) dmg = info.Durability >= 130 ? 4f : 3f;
        }

        Damage(m, dmg, _player.X, _player.Z);
        if (_player.Mode == Gamemode.Survival) inventory.DamageSelectedTool(1);
        return true;
    }

    /// <summary>Loot table on death: cows drop beef, zombies drop rotten flesh.</summary>
    public void DropLoot(Mob m)
    {
        if (_mining is null) return;
        int x = (int)Math.Floor(m.X), y = (int)Math.Floor(m.Y), z = (int)Math.Floor(m.Z);
        if (m.Type == MobType.Cow)
            _mining.SpawnDroppedItem(x, y, z, ItemId.BEEF, 1 + _rng.Next(2));
        else
            _mining.SpawnDroppedItem(x, y, z, ItemId.ROTTEN_FLESH, 1);
    }
}
