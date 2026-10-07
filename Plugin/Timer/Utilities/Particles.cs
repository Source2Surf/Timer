using System;
using System.Buffers.Binary;
using System.Text;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace Source2Surf.Timer.Utilities;

/// <summary>
///     Effects drawn for one player through the client's particle manager, with no entity: the server picks the index,
///     creates the effect at it from its resource ID and moves it by its control points.
/// </summary>
internal static class Particles
{
    // One message of each kind, reused: SendNetMessage serializes it before returning, and particles are only sent
    // from the game thread.
    private static readonly CUserMsg_ParticleManager CreateMessage = new ()
    {
        Type           = PARTICLE_MESSAGE.GameParticleManagerEventCreate,
        CreateParticle = new () { AttachType = (int) ParticleAttachmentType.WorldOrigin },
    };

    private static readonly CUserMsg_ParticleManager TransformMessage = new ()
    {
        Type                    = PARTICLE_MESSAGE.GameParticleManagerEventUpdateTransform,
        UpdateParticleTransform = new () { Position = new CMsgVector() },
    };

    private static readonly CUserMsg_ParticleManager DestroyMessage = new ()
    {
        Type            = PARTICLE_MESSAGE.GameParticleManagerEventDestroy,
        DestroyParticle = new () { DestroyImmediately = true },
    };

    private static readonly CUserMsg_ParticleManager ReleaseMessage = new ()
    {
        Type                 = PARTICLE_MESSAGE.GameParticleManagerEventRelease,
        ReleaseParticleIndex = new (),
    };

    // The same index on each recipient's client: one message, serialized once, for all of them.
    public static void Create(IModSharp sharp, RecipientFilter to, uint index, ulong effect)
    {
        CreateMessage.CreateParticle.ParticleNameIndex = effect;
        Send(sharp, to, index, CreateMessage);
    }

    public static void SetPoint(IModSharp sharp, RecipientFilter to, uint index, int controlPoint, Vector value, float interpolation = 0f)
    {
        var transform = TransformMessage.UpdateParticleTransform;
        transform.ControlPoint          = controlPoint;
        transform.Position.X            = value.X;
        transform.Position.Y            = value.Y;
        transform.Position.Z            = value.Z;
        transform.InterpolationInterval = interpolation;
        Send(sharp, to, index, TransformMessage);
    }

    // Gone at once, and the index free for the next effect.
    public static void Destroy(IModSharp sharp, RecipientFilter to, uint index)
    {
        Send(sharp, to, index, DestroyMessage);
        Send(sharp, to, index, ReleaseMessage);
    }

    private static void Send(IModSharp sharp, RecipientFilter to, uint index, CUserMsg_ParticleManager message)
    {
        message.Index = index;
        sharp.SendNetMessage(to, message);
    }

    /// <summary>
    ///     A resource's ID, as the resource system makes it from the name: MurmurHash64B with seed 0xEDABCDEF of the
    ///     lowercase name, with forward slashes and the source extension (".vpcf", not ".vpcf_c").
    /// </summary>
    internal static ulong ResourceId(string name)
    {
        const uint m = 0x5BD1E995;

        var data = Encoding.UTF8.GetBytes(name.ToLowerInvariant().Replace('\\', '/'));
        var left = data.Length;
        var at   = 0;

        unchecked
        {
            var h1 = 0xEDABCDEF ^ (uint) left;
            var h2 = 0u;

            for (; left >= 8; at += 8, left -= 8)
            {
                h1 = (h1 * m) ^ Mix(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at)));
                h2 = (h2 * m) ^ Mix(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 4)));
            }

            if (left >= 4)
            {
                h1   =  (h1 * m) ^ Mix(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at)));
                at   += 4;
                left -= 4;
            }

            if (left > 0)
            {
                if (left == 3)
                    h2 ^= (uint) data[at + 2] << 16;
                if (left >= 2)
                    h2 ^= (uint) data[at + 1] << 8;

                h2 ^= data[at];
                h2 *= m;
            }

            h1 ^= h2 >> 18;
            h1 *= m;
            h2 ^= h1 >> 22;
            h2 *= m;
            h1 ^= h2 >> 17;
            h1 *= m;
            h2 ^= h1 >> 19;
            h2 *= m;

            return ((ulong) h1 << 32) | h2;

            static uint Mix(uint k)
            {
                k *= m;
                k ^= k >> 24;

                return k * m;
            }
        }
    }
}
