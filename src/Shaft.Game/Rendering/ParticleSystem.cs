using System.Numerics;
using Shaft.Engine.Config;
using Shaft.Engine.Maths;
using Shaft.Engine.World;

namespace Shaft.Game.Rendering;

/// <summary>
/// The block-break puff from <c>spawnBreakParticles</c>/<c>updateParticles</c> in
/// <c>dist/mining.js</c>. Like the JS, this lives entirely in the presentation layer:
/// particles have no gameplay effect and are never saved.
/// </summary>
public sealed class ParticleSystem
{
    private const int MAX_PARTICLES = 512;

    private sealed class Particle
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public float Life;
        public float RotX, RotY;
        public Vector3 Color;      // linear, from BLOCK_INFO[id].color
    }

    private readonly List<Particle> _particles = new();
    private readonly VoxelWorld _world;
    private readonly Random _rng = new();

    public ParticleSystem(VoxelWorld world) => _world = world;

    public int Count => _particles.Count;

    /// <summary><c>spawnBreakParticles(x, y, z, blockId)</c> — eight 0.15 cubes.</summary>
    public void SpawnBreak(int x, int y, int z, byte blockId)
    {
        Colour.HexToLinear(BlockInfoTable.Get(blockId).Color, out float r, out float g, out float b);
        var colour = new Vector3(r, g, b);

        for (int i = 0; i < 8; i++)
        {
            if (_particles.Count >= MAX_PARTICLES) break;
            _particles.Add(new Particle
            {
                Position = new Vector3(
                    x + 0.5f + (float)(_rng.NextDouble() - 0.5) * 0.8f,
                    y + 0.5f + (float)(_rng.NextDouble() - 0.5) * 0.8f,
                    z + 0.5f + (float)(_rng.NextDouble() - 0.5) * 0.8f),
                Velocity = new Vector3(
                    (float)(_rng.NextDouble() - 0.5) * 3f,
                    (float)_rng.NextDouble() * 4f + 1f,
                    (float)(_rng.NextDouble() - 0.5) * 3f),
                Life = 1f,
                Color = colour,
            });
        }
    }

    /// <summary><c>updateParticles(dt)</c>, including the solid-block landing snap.</summary>
    public void Update(float dt)
    {
        for (int i = _particles.Count - 1; i >= 0; i--)
        {
            Particle p = _particles[i];
            p.Velocity.Y -= 20f * dt;
            p.Position += p.Velocity * dt;

            int bx = (int)MathF.Floor(p.Position.X);
            int by = (int)MathF.Floor(p.Position.Y);
            int bz = (int)MathF.Floor(p.Position.Z);
            if (_world.IsSolid(bx, by, bz))
            {
                p.Velocity = Vector3.Zero;
                p.Position.Y = by + 1.08f;
            }

            p.Life -= dt * 1.2f;
            p.RotX += dt * 5f;
            p.RotY += dt * 4f;

            if (p.Life <= 0f)
            {
                _particles[i] = _particles[_particles.Count - 1];
                _particles.RemoveAt(_particles.Count - 1);
            }
        }
    }

    /// <summary>Scale for <c>mesh.scale.setScalar(Math.max(.01, life))</c>.</summary>
    public float ScaleAt(int i) => Math.Max(0.01f, _particles[i].Life);
    public Vector3 PositionAt(int i) => _particles[i].Position;
    public Vector3 ColorAt(int i) => _particles[i].Color;
    public (float X, float Y) RotationAt(int i) => (_particles[i].RotX, _particles[i].RotY);

    public void Clear() => _particles.Clear();
}
