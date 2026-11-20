using System;
using System.Collections.Generic;
using System.Numerics;

namespace Redline;

internal enum Part : byte { Body, LeftLeg, RightLeg, LeftArm, RightArm, Head }
internal enum MajorCut : byte { None, Waist, Head }

internal static class ImpactCuts
{
    internal readonly record struct Ray(Vector3 Point, Vector3 Direction)
    {
        public float DistanceTo(Vector3 point)
        {
            float depth = Clamp(Vector3.Dot(point - Point, Direction), -0.28f, 2.8f);
            return Vector3.Distance(point, Point + Direction * depth);
        }
    }

    internal readonly record struct Zone(Part Part, Vector3 From, Vector3 To, float Radius);
    internal readonly record struct Candidate(Part Part, float Gap);
    internal readonly record struct UnlocatedCut(Part Part, float Cost, float Chance);

    private const float MinimumUnlocatedStrike = 0.08f;

    public static Candidate[] Rank(Ray[] rays, List<Zone> zones)
    {
        var gaps = new float[6];
        for (int i = 0; i < gaps.Length; i++)
            gaps[i] = float.PositiveInfinity;
        foreach (var zone in zones)
        {
            int category = (int)zone.Part;
            foreach (var ray in rays)
            {
                for (int i = 0; i <= 6; i++)
                {
                    var point = Vector3.Lerp(zone.From, zone.To, i / 6f);
                    gaps[category] = Math.Min(gaps[category], ray.DistanceTo(point) - zone.Radius);
                }
            }
        }

        var ranked = new Candidate[gaps.Length];
        for (int i = 0; i < gaps.Length; i++)
            ranked[i] = new Candidate((Part)i, gaps[i]);
        Array.Sort(ranked, (a, b) =>
        {
            int gap = a.Gap.CompareTo(b.Gap);
            return gap == 0 ? a.Part.CompareTo(b.Part) : gap;
        });
        return ranked;
    }

    public static Part[] Choose(Candidate[] ranked, float[] cumulative, float strength, bool heavy, bool spread,
        bool terminal, int missing)
    {
        int limit = strength >= 0.66f && (spread || terminal && heavy) ? 2 : 1;
        float reach = Reach(heavy, spread);
        float closest = ranked[0].Gap;
        var selection = new List<Part>(limit);
        foreach (var candidate in ranked)
        {
            var part = candidate.Part;
            if (candidate.Gap > reach || candidate.Gap > closest + reach + 0.12f || selection.Count == limit)
                break;
            if (!terminal && !spread && part != ranked[0].Part)
                break;
            if (!CanCut(part, terminal, missing))
                continue;
            float value = strength * (part == ranked[0].Part ? 1f : spread ? 0.85f : 0.67f);
            cumulative[(int)part] += value;
            float required = Threshold(part, heavy) + (selection.Count != 0 ? 0.10f : 0);
            if (cumulative[(int)part] >= required)
            {
                selection.Add(part);
                missing |= 1 << (int)part;
            }
        }
        return selection.ToArray();
    }

    public static bool HasLimbContact(Candidate[] ranked, bool heavy, bool spread, bool terminal, int missing) =>
        CanCut(ranked[0].Part, terminal, missing) && ranked[0].Gap <= Reach(heavy, spread);

    public static bool HasLegCutContact(Candidate[] ranked, int missing)
    {
        int contacted = 0;
        foreach (var candidate in ranked)
            if (candidate.Part is Part.LeftLeg or Part.RightLeg && candidate.Gap <= 0f)
                contacted |= 1 << (int)candidate.Part;
        return (contacted & ~missing) != 0 && ((contacted | missing) & InjuryRules.Legs) == InjuryRules.Legs;
    }

    public static Part[] ChooseLegs(Candidate[] ranked, float[] cumulative, float strength, bool heavy, int missing)
    {
        var result = new List<Part>(2);
        foreach (var candidate in ranked)
        {
            int index = (int)candidate.Part;
            if (candidate.Part is not (Part.LeftLeg or Part.RightLeg) || candidate.Gap > 0f || (missing & (1 << index)) != 0)
                continue;
            cumulative[index] += strength;
            if (cumulative[index] >= Threshold(candidate.Part, heavy))
                result.Add(candidate.Part);
        }
        return result.ToArray();
    }

    public static bool CanFallbackToLimb(bool terminal, bool heavy, float strength) =>
        terminal && heavy && strength >= 0.62f;

    public static MajorCut RollMajorCut(float strength, float waistChance, bool waistContact, bool headContact,
        bool bodyOrUnlocated, bool headAvailable, float waistRoll, float headRoll)
    {
        float energy = Clamp((strength - 0.54f) / 0.86f, 0f, 1f);
        if (headAvailable && headContact && headRoll < 0.88f + 0.11f * energy)
            return MajorCut.Head;
        float waistBias = waistContact ? 1.20f : bodyOrUnlocated ? 0.84f : 0.26f;
        if (waistRoll < Clamp(waistChance * waistBias * (0.84f + 0.16f * energy), 0f, 1f))
            return MajorCut.Waist;
        float headChance = bodyOrUnlocated ? 0.34f + 0.24f * energy : 0.12f + 0.15f * energy;
        return headAvailable && headRoll < headChance ? MajorCut.Head : MajorCut.None;
    }

    public static Part[] CompanionOrder(Candidate[] ranked, int missing, bool upper, bool right)
    {
        var limbs = new List<Part>(4);
        var nearArm = right ? Part.RightArm : Part.LeftArm;
        var nearLeg = right ? Part.RightLeg : Part.LeftLeg;
        var farArm = right ? Part.LeftArm : Part.RightArm;
        var farLeg = right ? Part.LeftLeg : Part.RightLeg;
        if (ranked != null)
            foreach (var candidate in ranked)
                if (!float.IsInfinity(candidate.Gap)) Add(candidate.Part);
        if (upper) { Add(nearArm); Add(farArm); Add(nearLeg); Add(farLeg); }
        else { Add(nearLeg); Add(nearArm); Add(farLeg); Add(farArm); }
        return limbs.ToArray();

        void Add(Part part)
        {
            if (part != Part.Head && CanCut(part, true, missing) && !limbs.Contains(part))
                limbs.Add(part);
        }
    }

    public static UnlocatedCut RollUnlocated(float[] cumulative, float reserve, float strength, bool heavy,
        bool terminal, int missing, int available, float side, float roll, float pick)
    {
        if (strength < MinimumUnlocatedStrike)
            return default;
        var weights = new float[cumulative.Length];
        float total = 0;
        for (int index = (int)Part.LeftLeg; index <= (int)Part.Head; index++)
        {
            var part = (Part)index;
            if ((available & (1 << index)) == 0 || !CanCut(part, terminal, missing) || Cost(part) > reserve)
                continue;
            float nearSide = part is Part.RightLeg or Part.RightArm ? side : -side;
            weights[index] = part == Part.Head ? 0.45f : 1f + nearSide * 0.35f;
            weights[index] += cumulative[index] * 1.25f;
            total += weights[index];
        }
        if (total <= 0)
            return default;
        float chosen = Clamp(pick, 0, 0.999999f) * total;
        for (int index = (int)Part.LeftLeg; index <= (int)Part.Head; index++)
        {
            if (weights[index] == 0)
                continue;
            chosen -= weights[index];
            if (chosen > 0)
                continue;
            var part = (Part)index;
            float cost = Cost(part);
            float chance = Math.Min(0.95f, 0.27f + (heavy ? 0.16f : 0f) + (terminal ? 0.14f : 0f)
                + 0.48f * (reserve - cost) + 0.26f * Math.Min(1f, strength));
            return roll < chance ? new(part, cost, chance) : new(Part.Body, 0f, chance);
        }
        return default;

        float Cost(Part part) => Math.Max(MinimumUnlocatedStrike, Threshold(part, heavy) - cumulative[(int)part]);
    }

    private static float Reach(bool heavy, bool spread) => spread ? 0.72f : heavy ? 0.40f : 0.25f;

    private static float Threshold(Part part, bool heavy) => (heavy ? 0.28f : 0.40f) + (part == Part.Head ? 0.15f : 0);

    private static bool CanCut(Part part, bool terminal, int missing)
    {
        return part != Part.Body && !HasMissing(part)
            && (terminal || part != Part.Head && (part != Part.LeftArm || !HasMissing(Part.RightArm))
                && (part != Part.RightArm || !HasMissing(Part.LeftArm)));

        bool HasMissing(Part region) => (missing & (1 << (int)region)) != 0;
    }

    private static float Clamp(float value, float min, float max) => Math.Max(min, Math.Min(max, value));
}
