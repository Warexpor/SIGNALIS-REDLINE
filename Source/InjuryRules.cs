using System;

namespace Redline;

internal static class InjuryRules
{
    public const int Arms = (1 << (int)Part.LeftArm) | (1 << (int)Part.RightArm);
    public const int Legs = (1 << (int)Part.LeftLeg) | (1 << (int)Part.RightLeg);

    public static float Strength(float damage, int fullHealth) =>
        Math.Max(0, Math.Min(1.4f, damage / Math.Max(1, fullHealth)));

    public static bool CanLaunch(bool heavy, float damage, int fullHealth) =>
        heavy && damage >= 20f && Strength(damage, fullHealth) >= 0.54f;

    public static bool CanOverkill(bool heavy, float priorHealth, int incomingHealth, int fullHealth) =>
        heavy && priorHealth > 0 && incomingHealth <= 0 && priorHealth - incomingHealth >= Math.Max(35, fullHealth);

    public static float OverkillChance(float priorHealth, int incomingHealth, int fullHealth, bool rifle = false)
    {
        float health = Math.Max(1, fullHealth);
        float excess = Math.Max(0, (priorHealth - incomingHealth - Math.Max(35, fullHealth)) / health);
        float freshChance = rifle ? Math.Min(0.72f, 0.28f + 0.34f * excess)
            : Math.Min(0.45f, 0.12f + 0.26f * excess);
        float missingHealth = Math.Max(0, 1f - priorHealth / health);
        float injury = Math.Min(1f, missingHealth / 0.75f);
        return freshChance + (1f - freshChance) * injury;
    }

    public static float LaunchPush(float strength, float upright, bool rifle)
    {
        float energy = Math.Max(0f, Math.Min(1f, (strength - 0.54f) / 0.86f));
        float grounded = 3f + 1.2f * energy;
        float standing = 5.7f + 2f * energy;
        if (rifle)
        {
            grounded += 0.65f;
            standing += 1.1f;
        }
        return grounded + (standing - grounded) * upright;
    }

    public static float DeliveredDamage(float priorHealth, int incomingHealth, int resolvedHealth) =>
        Math.Max(0, priorHealth - Math.Min(incomingHealth, resolvedHealth));

    public static bool IsFinalDeath(bool dead, int health, int revives, bool queuedForRespawn, bool permanent) =>
        dead && health <= 0 && (permanent || revives <= 0 && !queuedForRespawn);

    public static float CrawlSpeed(int missing)
    {
        float speed = (missing & Legs) == Legs ? 0.26f : 0.34f;
        return (missing & Arms) == 0 ? speed : speed * 0.78f;
    }

    public static Part SwipeArm(int missing, int weaponArms)
    {
        int available = Arms & ~missing;
        int unarmed = available & ~weaponArms;
        int preferred = unarmed != 0 ? unarmed : available;
        if ((preferred & (1 << (int)Part.RightArm)) != 0)
            return Part.RightArm;
        return preferred != 0 ? Part.LeftArm : Part.Body;
    }

    public static bool UsesSwipe(int missing, int weaponArms, bool crawling) =>
        crawling || (missing & (weaponArms != 0 ? weaponArms : Arms)) != 0;
}
