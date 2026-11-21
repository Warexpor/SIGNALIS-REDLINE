using System;
using System.IO;
using UnhollowerRuntimeLib;
using UnityEngine;

namespace Redline;

// Caches the six Ariane portraits from the installed game files on first
// launch so players never run an extractor script. Replaces extract_ariane.py.
public sealed partial class RedlineMod
{
    private void EnsureArianeCache()
    {
        try
        {
            string directory = Path.Combine("UserData", "RedlineAriane");
            bool complete = true;
            foreach (string name in ArianeFrameNames)
            {
                if (!File.Exists(Path.Combine(directory, name + ".png")))
                {
                    complete = false;
                    break;
                }
            }
            if (complete)
                return;
            Directory.CreateDirectory(directory);

            string bundlePath = Path.Combine(Application.dataPath, "data.unity3d");
            var bundle = AssetBundle.LoadFromFile(bundlePath);
            if (bundle == null)
            {
                Log("ariane cache bundle missing");
                return;
            }
            try
            {
                int saved = 0;
                foreach (string name in ArianeFrameNames)
                {
                    string path = Path.Combine(directory, name + ".png");
                    if (File.Exists(path))
                        continue;
                    var asset = bundle.LoadAsset(name, Il2CppType.Of<Texture2D>());
                    var texture = asset?.TryCast<Texture2D>();
                    if (texture == null)
                    {
                        Log($"ariane cache miss {name}");
                        continue;
                    }
                    byte[] png = EncodeAriane(texture);
                    if (png == null)
                    {
                        Log($"ariane cache unreadable {name}");
                        continue;
                    }
                    File.WriteAllBytes(path, png);
                    saved++;
                }
                Log($"ariane cache saved={saved}");
                if (saved > 0)
                    LoggerInstance.Msg($"REDLINE cached {saved} Ariane images.");
            }
            finally
            {
                bundle.Unload(false);
            }
        }
        catch (Exception error)
        {
            Log($"ariane cache failed {error.GetType().Name}: {error.Message}");
        }
    }

    private static byte[] EncodeAriane(Texture2D texture)
    {
        try
        {
            return ImageConversion.EncodeToPNG(texture);
        }
        catch
        {
            // Compressed bundle textures are usually not CPU-readable; blit through a target.
        }
        try
        {
            var target = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32);
            try
            {
                Graphics.Blit(texture, target);
                var previous = RenderTexture.active;
                RenderTexture.active = target;
                try
                {
                    var copy = new Texture2D(texture.width, texture.height, TextureFormat.RGB24, false);
                    copy.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                    copy.Apply();
                    return ImageConversion.EncodeToPNG(copy);
                }
                finally
                {
                    RenderTexture.active = previous;
                }
            }
            finally
            {
                RenderTexture.ReleaseTemporary(target);
            }
        }
        catch
        {
            return null;
        }
    }
}
