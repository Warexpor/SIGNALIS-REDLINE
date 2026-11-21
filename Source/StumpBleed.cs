using System;

namespace Redline;

internal sealed class StumpBleed(float started, bool heavy)
{
    private readonly float duration = heavy ? 21f : 17f;
    private float nextPulse = started + 0.20f;
    private float downedAt = float.PositiveInfinity;

    public bool PlayedSound;

    public bool Finished(float now) => now - started >= duration || now - downedAt >= 6.8f;

    public float Pulse(float now, bool downed, float movement)
    {
        if (downed)
            downedAt = Math.Min(downedAt, now);
        if (now < nextPulse || Finished(now))
            return 0;

        float fresh = 1f - (now - started) / duration;
        float pressure = 0.14f + fresh * fresh * (heavy ? 0.86f : 0.72f);
        if (downedAt < float.PositiveInfinity)
            pressure *= 0.60f * (1f - (now - downedAt) / 6.8f);
        nextPulse = now + 0.29f + (1f - pressure) * 0.58f - movement * 0.10f;
        return pressure;
    }
}
