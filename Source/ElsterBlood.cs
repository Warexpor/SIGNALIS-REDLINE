using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;
using Point = System.Numerics.Vector3;
using UV = System.Numerics.Vector2;

namespace Redline;

internal sealed class ElsterBlood(RedlineSettings settings, Action<string> log)
{
    private readonly System.Random random = new();
    private readonly Queue<Spray> pending = new(6);
    private readonly List<Surface> surfaces = new(4);
    private readonly Dictionary<int, Color32[]> originals = new();
    private Mesh sample;
    private float nextFade;
    private bool rescan;

    public void Queue(Vector3 source, bool heavy, bool stomp, float amount)
    {
        amount *= RedlineSettings.Factor(settings.ElsterStains);
        var player = PlayerState.pcs;
        if (amount <= 0 || !player || !player.isActiveAndEnabled || PlayerState.noplayer || PlayerState.menu
            || PlayerState.cutscene || PlayerState.gameOver || pending.Count >= 6)
            return;
        var origin = player._rigidbody ? player._rigidbody.position : player.transform.position;
        var toward = Battlefield.OnPlane(source - origin);
        float distance = toward.magnitude;
        float reach = ClothingBlood.Reach(distance, heavy);
        if (reach <= 0.025f)
            return;

        var torso = origin + Battlefield.Up * 5.4f;
        var from = Battlefield.AtHeight(source, Mathf.Max(Battlefield.Height(source), Battlefield.Height(origin) + 1.1f));
        if (Physics.Linecast(from, torso, Battlefield.RoomSolids, QueryTriggerInteraction.Ignore))
            return;
        toward = distance > 0.05f ? toward / distance : Battlefield.OnPlane(player.transform.forward).normalized;
        float strength = Mathf.Clamp01((0.22f + 0.78f * reach) * Mathf.Sqrt(amount / 1.25f)
            * (stomp ? 1.1f : heavy ? 1.0f : 0.83f));
        float height = stomp ? 2.6f : Mathf.Clamp(0.61f * Battlefield.Height(source - origin) + 2.15f, 3.6f, 6.7f);
        pending.Enqueue(new Spray(player, origin, Rig.ToPoint(toward), height, strength, distance, stomp));
    }

    public void Tick(float amount)
    {
        if (PlayerState.gameOver || amount <= 0 || RedlineSettings.Factor(settings.ElsterStains) <= 0)
        {
            if (pending.Count != 0 || surfaces.Count != 0) Clear();
            return;
        }
        if (pending.Count != 0)
        {
            var hit = pending.Dequeue();
            if (hit.Player && hit.Player == PlayerState.pcs && hit.Player.isActiveAndEnabled)
                Apply(hit);
        }
        if (surfaces.Count == 0 || Time.time < nextFade)
            return;
        nextFade = Time.time + 0.65f;
        if (rescan && PlayerState.pcs && PlayerState.pcs.isActiveAndEnabled)
        {
            Rebind(PlayerState.pcs);
            rescan = false;
        }
        for (int i = surfaces.Count - 1; i >= 0; i--)
        {
            var surface = surfaces[i];
            if (surface.Fade(Time.time))
            {
                surface.FollowMaterial();
                continue;
            }
            surface.Dispose();
            surfaces.RemoveAt(i);
        }
    }

    public void SceneChanged(bool reset)
    {
        pending.Clear();
        if (reset) Clear();
        else rescan = true;
    }

    private void Rebind(AlternatePlayerController player)
    {
        foreach (var renderer in player.GetComponentsInChildren<SkinnedMeshRenderer>(false))
        {
            if (!Supported(renderer))
                continue;
            var mesh = renderer.sharedMesh;
            var material = renderer.sharedMaterial;
            var slot = surfaces.Find(s => s.Matches(renderer, mesh, material));
            if (slot != null)
                slot.Bind(renderer);
        }
    }

    private void Apply(Spray hit)
    {
        var pattern = new ClothingBlood(random, hit.Height, hit.Strength, hit.Stomp);
        int pixels = 0;
        foreach (var renderer in hit.Player.GetComponentsInChildren<SkinnedMeshRenderer>(false))
        {
            if (!Supported(renderer))
                continue;
            var mesh = renderer.sharedMesh;
            var material = renderer.sharedMaterial;
            var surface = surfaces.Find(s => s.Matches(renderer, mesh, material));
            bool owned = surface == null;
            if (owned)
            {
                var texture = material.mainTexture;
                if (!texture || texture.width > 512 || texture.height > 512 ||
                    !texture.name.StartsWith("elster_", StringComparison.OrdinalIgnoreCase))
                    continue;
                surface = new Surface(mesh, material, texture, ReadOriginal(texture));
            }
            try
            {
                var ink = Project(renderer, surface, hit, pattern);
                if (ink.Count == 0)
                    continue;
                surface.Add(ink, Time.time, 24f + hit.Strength * 15f + (float)random.NextDouble() * 7f);
                if (renderer.sharedMaterial != surface.Painted)
                    surface.Bind(renderer);
                if (owned)
                {
                    surfaces.Add(surface);
                    owned = false;
                }
                pixels += ink.Count;
            }
            finally
            {
                if (owned) surface.Dispose();
            }
        }
        nextFade = Time.time + 0.5f;
        log($"elster blood distance={hit.Distance:F1} strength={hit.Strength:F2} stomp={hit.Stomp} texels={pixels}");
    }

    private static bool Supported(SkinnedMeshRenderer renderer)
    {
        if (!renderer || !renderer.enabled || !renderer.sharedMaterial
            || renderer.forceRenderingOff && !renderer.transform.Find("REDLINE Cut Skin"))
            return false;
        var mesh = renderer.sharedMesh;
        return mesh && mesh.isReadable && mesh.subMeshCount == 1 && mesh.name is
            "Body" or "Body_Suit" or "BodyCrippled" or "BodyUncrippled_Armless" or "Chest_Armor" or "HairHead" or "Helmet";
    }

    private List<ClothingBlood.Ink> Project(SkinnedMeshRenderer renderer, Surface surface, Spray hit, ClothingBlood pattern)
    {
        if (!sample) sample = new Mesh { name = "REDLINE Elster Splash Pose" };
        renderer.BakeMesh(sample);
        var vertices = sample.vertices;
        if (vertices.Length == 0 || vertices.Length != surface.Uv.Length)
            return new List<ClothingBlood.Ink>();
        var normals = sample.normals;
        var matrix = renderer.localToWorldMatrix;
        var relative = new System.Numerics.Matrix4x4(
            matrix.m00, matrix.m10, matrix.m20, 0,
            matrix.m01, matrix.m11, matrix.m21, 0,
            matrix.m02, matrix.m12, matrix.m22, 0,
            matrix.m03 - hit.Origin.x, matrix.m13 - hit.Origin.y, matrix.m23 - hit.Origin.z, 1);
        var positions = new Point[vertices.Length];
        var directions = new Point[vertices.Length];
        for (int i = 0; i < positions.Length; i++)
        {
            positions[i] = Point.Transform(Rig.ToPoint(vertices[i]), relative);
            if (i < normals.Length)
                directions[i] = Point.TransformNormal(Rig.ToPoint(normals[i]), relative);
        }
        return pattern.Project(positions, directions, surface.Uv, surface.Triangles, hit.Toward, surface.Width, surface.Height);
    }

    private Color32[] ReadOriginal(Texture source)
    {
        int id = source.GetInstanceID();
        if (originals.TryGetValue(id, out var colors))
            return colors;

        var previous = RenderTexture.active;
        var target = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
        Texture2D copy = null;
        try
        {
            Graphics.Blit(source, target);
            RenderTexture.active = target;
            copy = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            copy.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
            var readback = copy.GetPixels32();
            colors = new Color32[readback.Length];
            for (int i = 0; i < colors.Length; i++)
                colors[i] = readback[i];
            originals.Add(id, colors);
            return colors;
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
            if (copy) Object.Destroy(copy);
        }
    }

    public void Clear()
    {
        pending.Clear();
        foreach (var surface in surfaces)
            surface.Dispose();
        surfaces.Clear();
        originals.Clear();
        if (sample) Object.Destroy(sample);
        sample = null;
        rescan = false;
        nextFade = 0;
    }

    private readonly record struct Spray(AlternatePlayerController Player, Vector3 Origin, Point Toward,
        float Height, float Strength, float Distance, bool Stomp);

    private sealed class Surface
    {
        public readonly Mesh Mesh;
        public Material Painted { get; private set; }
        public readonly UV[] Uv;
        public readonly int[] Triangles;
        public readonly int Width;
        public readonly int Height;
        public SkinnedMeshRenderer Renderer { get; private set; }
        private readonly Texture2D texture;
        private readonly Texture source;
        private readonly Vector2 scale;
        private readonly Vector2 offset;
        private Material original;
        private readonly Color32[] clean;
        private readonly Color32[] pixels;
        private readonly List<Layer> layers = new(6);

        public Surface(Mesh mesh, Material material, Texture source, Color32[] clean)
        {
            Mesh = mesh;
            original = material;
            this.source = source;
            Width = source.width;
            Height = source.height;
            this.clean = clean;
            pixels = new Color32[clean.Length];
            var uv = mesh.uv;
            scale = material.mainTextureScale;
            offset = material.mainTextureOffset;
            Uv = new UV[uv.Length];
            for (int i = 0; i < Uv.Length; i++)
                Uv[i] = new UV(uv[i].x * scale.x + offset.x, uv[i].y * scale.y + offset.y);
            var indices = mesh.triangles;
            Triangles = new int[indices.Length];
            for (int i = 0; i < Triangles.Length; i++)
                Triangles[i] = indices[i];
            texture = new Texture2D(Width, Height, TextureFormat.RGBA32, source.mipmapCount > 1)
            {
                name = "REDLINE Elster Stains", filterMode = source.filterMode, wrapMode = source.wrapMode,
                anisoLevel = source.anisoLevel
            };
            Painted = new Material(material) { name = "REDLINE Bloodstained Elster", mainTexture = texture };
        }

        public bool Matches(SkinnedMeshRenderer renderer, Mesh mesh, Material material) => mesh == Mesh
            && (!Renderer || Renderer == renderer) && (material == Painted || Compatible(material));

        private bool Compatible(Material material) => material && material.mainTexture == source
            && material.mainTextureScale == scale && material.mainTextureOffset == offset;

        public void FollowMaterial()
        {
            if (Renderer && Renderer.sharedMesh == Mesh && Renderer.sharedMaterial != Painted && Compatible(Renderer.sharedMaterial))
                Bind(Renderer);
        }

        public void Bind(SkinnedMeshRenderer renderer)
        {
            var material = renderer.sharedMaterial;
            if (material != original && material != Painted && Compatible(material))
            {
                var old = Painted;
                Painted = new Material(material) { name = "REDLINE Bloodstained Elster", mainTexture = texture };
                original = material;
                Object.Destroy(old);
            }
            renderer.sharedMaterial = Painted;
            Renderer = renderer;
        }

        public void Add(List<ClothingBlood.Ink> ink, float now, float lifetime)
        {
            if (layers.Count == 6) layers.RemoveAt(0);
            layers.Add(new Layer(ink, now, lifetime));
            Fade(now);
        }

        public bool Fade(float now)
        {
            layers.RemoveAll(layer => now - layer.Start >= layer.Lifetime);
            if (layers.Count == 0)
                return false;
            Array.Copy(clean, pixels, clean.Length);
            foreach (var layer in layers)
            {
                float age = now - layer.Start;
                float residue = ClothingBlood.Residue(age, layer.Lifetime) / 255f;
                float wet = ClothingBlood.Wetness(age);
                var (red, green, blue) = BloodTexture.Crimson(0.73f + 0.39f * wet, 0.08f * wet, false);
                foreach (var spot in layer.Ink)
                {
                    var color = pixels[spot.Pixel];
                    float coverage = spot.Coverage * residue * (0.80f + 0.20f * wet);
                    pixels[spot.Pixel] = new Color32(Blend(color.r, red * 1.22f, coverage),
                        Blend(color.g, green, coverage), Blend(color.b, blue, coverage), color.a);
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply();
            return true;

            static byte Blend(byte source, float pigment, float coverage) => (byte)(source + (pigment - source) * coverage);
        }

        public void Dispose()
        {
            if (Renderer && Renderer.sharedMaterial == Painted)
                Renderer.sharedMaterial = original;
            if (Painted) Object.Destroy(Painted);
            if (texture) Object.Destroy(texture);
        }

        private readonly record struct Layer(List<ClothingBlood.Ink> Ink, float Start, float Lifetime);
    }
}
