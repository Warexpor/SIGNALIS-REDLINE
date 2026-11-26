using System;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace Redline;

internal static class MagCaches
{
    public static bool TrySpawn(Items.itemlist ammo, int count, Vector3 position, Transform parent, Action<string> log)
    {
        var item = InventoryManager.getItem(ammo);
        if (!item)
        {
            log($"mag cache skipped: item catalog has no entry for {ammo}");
            return false;
        }

        var drop = new GameObject("REDLINE Mag Cache");
        drop.SetActive(false);
        try
        {
            int layer = LayerMask.NameToLayer("Interactables");
            drop.layer = layer >= 0 ? layer : 19;
            drop.transform.SetParent(parent, true);
            drop.transform.position = position;
            var collider = drop.AddComponent<BoxCollider2D>();
            collider.size = new Vector2(6.4f, 6.4f);
            var prompt = new GameObject("Prompt").transform;
            prompt.SetParent(drop.transform, false);
            prompt.localPosition = Battlefield.Up * 0.8f;

            var interaction = drop.AddComponent<Interaction>();
            interaction.type = Interaction.interType.take;
            interaction.anyAngle = true;
            interaction.pointOfInterest = prompt;
            interaction.onTriggeredEvent = new UnityEvent();
            drop.AddComponent<UniqueId>().id = "redline.mag." + Guid.NewGuid().ToString("N");
            var pickup = drop.AddComponent<ItemPickup>();
            pickup.inter = interaction;
            pickup._item = item;
            pickup._itemEnum = ammo;
            pickup.count = count;
            pickup.firstObserved = true; // Keep the cache quantity when the game first examines it.
            pickup.pauseGame = true;
            pickup.focusCamera = true;
            pickup.showItemView = true;
            pickup.message = string.Empty;
            pickup._dialogue = DialoguerDialogues.Pickup_dialogue;
            pickup._dialogueLong = DialoguerDialogues.Pickup_dialogue_long;
            pickup._cannotDialogue = DialoguerDialogues.Pickup_cantCarry;
            pickup._nospaceDialogue = DialoguerDialogues.Pickup_noSlots;
            pickup.onPickup = new UnityEvent();
            drop.AddComponent<ItemPickupName>();

            var model = AddVisual(drop.transform, item);
            if (!model)
            {
                log($"mag cache skipped: item catalog has no visual for {ammo}");
                Object.Destroy(drop);
                return false;
            }
            drop.SetActive(true);
            SettleVisual(model, position);
            pickup.count = count;
            pickup.firstObserved = true;
            log($"mag cache ammo={ammo} rounds={count} spot={Battlefield.Format(position)} source=catalog");
            return true;
        }
        catch
        {
            drop.SetActive(false);
            Object.Destroy(drop);
            throw;
        }
    }

    private static Transform AddVisual(Transform parent, AnItem item)
    {
        if (item.Image3D)
        {
            var model = Object.Instantiate(item.Image3D, parent, false);
            model.name = "Model3D";
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.Euler(-90f, 0f, -31f);
            foreach (var script in model.GetComponentsInChildren<MonoBehaviour>(true))
                Object.DestroyImmediate(script);
            foreach (var collider in model.GetComponentsInChildren<Collider>(true))
                collider.enabled = false;
            foreach (var collider in model.GetComponentsInChildren<Collider2D>(true))
                collider.enabled = false;
            foreach (var part in model.GetComponentsInChildren<Transform>(true))
                part.gameObject.layer = parent.gameObject.layer;
            model.SetActive(true);
            return model.transform;
        }
        var sprite = item.worldSprite ? item.worldSprite : item.Image;
        if (!sprite)
            return null;
        var visual = new GameObject("Model3D") { layer = parent.gameObject.layer };
        visual.transform.SetParent(parent, false);
        visual.AddComponent<SpriteRenderer>().sprite = sprite;
        return visual.transform;
    }

    private static void SettleVisual(Transform model, Vector3 floor)
    {
        var renderers = model.GetComponentsInChildren<Renderer>(false);
        if (renderers.Length == 0)
            return;
        Bounds bounds = Measure();
        var size = bounds.size;
        if (size.x < size.z && size.x <= size.y)
            model.rotation = Quaternion.AngleAxis(90f, Vector3.up) * model.rotation;
        else if (size.y < size.z && size.y < size.x)
            model.rotation = Quaternion.AngleAxis(90f, Vector3.right) * model.rotation;
        float width = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
        if (width > 0.01f)
            model.localScale *= 1.25f / width;
        bounds = Measure();
        model.position += floor - new Vector3(bounds.center.x, bounds.center.y, bounds.max.z);

        Bounds Measure()
        {
            var combined = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                combined.Encapsulate(renderers[i].bounds);
            return combined;
        }
    }
}
