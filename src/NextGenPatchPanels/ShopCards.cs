using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NextGenPatchPanels
{
    // Copies a vanilla shop card for each trunk panel and trunk cable, into a new row directly below the
    // vanilla card's row (patch panels, cables). Unlock state saves natively: the game keeps a
    // guid -> unlocked map and ignores unknown guids.
    internal static class ShopCards
    {
        const string GuidPrefix = "nextgenpatchpanels-";
        const int RowCapacity = 4; // vanilla shop rows show at most four cards

        sealed class Card
        {
            public string Key;
            public string Label;
            public ShopItemSO SO;
            public PlayerManager.ObjectInHand Type;
            public int TwinItemID;
            public ShopItem Vanilla;
        }

        static readonly Dictionary<string, Card> Cards = new();
        static readonly HashSet<PlayerManager.ObjectInHand> TextLogged = new();

        public static void EnsureCards()
        {
            var shop = MainGameManager.instance?.computerShop;
            var items = shop?.shopItems;
            if (items == null) return;

            for (int i = 0; i < items.Length; i++)
                if (items[i] != null && items[i].guid != null && items[i].guid.StartsWith(GuidPrefix))
                    return; // this shop already has our cards

            Cards.Clear();
            foreach (var t in Templates.Types)
                if (t.SO != null) Cards[t.Key] = new Card { Key = t.Key, Label = t.Label, SO = t.SO, Type = PlayerManager.ObjectInHand.PatchPanel, TwinItemID = t.TwinIndex };
            foreach (var s in Spools.Types)
                if (s.SO != null) Cards[s.Key] = new Card { Key = s.Key, Label = s.Label, SO = s.SO, Type = PlayerManager.ObjectInHand.CableSpinner, TwinItemID = s.TwinSO.itemID };

            var added = new List<ShopItem>();
            var rows = new Dictionary<int, Transform>(); // vanilla row -> our row below it
            foreach (var c in Cards.Values)
            {
                for (int i = 0; i < items.Length && c.Vanilla == null; i++)
                {
                    var so = items[i] == null ? null : items[i].shopItemSO;
                    if (so != null && so.itemType == c.Type && so.itemID == c.TwinItemID) c.Vanilla = items[i];
                }
                if (c.Vanilla == null)
                {
                    NextGenPatchPanelsMod.Log.Warning($"No vanilla shop card for {c.Type} {c.TwinItemID}; {c.Label} will not be sold.");
                    continue;
                }

                var vanillaRow = c.Vanilla.transform.parent;
                if (!rows.TryGetValue(vanillaRow.GetInstanceID(), out var row) || CardCount(row) >= RowCapacity)
                    row = AddRowBelow(row ?? vanillaRow);
                rows[vanillaRow.GetInstanceID()] = row;

                // Clone under the inactive holder so Awake sees our SO and guid, then move into the row.
                var go = Object.Instantiate(c.Vanilla.gameObject, Templates.Holder);
                go.name = $"ShopCard.NextGenPatchPanels.{c.Key}";
                var card = go.GetComponent<ShopItem>();
                card.shopItemSO = c.SO;
                card.guid = GuidPrefix + c.Key;
                card.isUnlocked = false;
                card.itemDisplayName = c.Label;
                go.transform.SetParent(row, false);
                go.transform.SetAsLastSibling();
                added.Add(card);
            }
            if (added.Count == 0) return;

            var extended = new Il2CppReferenceArray<ShopItem>(items.Length + added.Count);
            for (int i = 0; i < items.Length; i++) extended[i] = items[i];
            for (int i = 0; i < added.Count; i++) extended[items.Length + i] = added[i];
            shop.shopItems = extended;
            NextGenPatchPanelsMod.Log.Msg($"Added {added.Count} shop cards.");
        }

        static int CardCount(Transform row)
        {
            int n = 0;
            for (int i = 0; i < row.childCount; i++)
                if (row.GetChild(i).GetComponent<ShopItem>() != null) n++;
            return n;
        }

        // An empty copy of the row (same layout and size), inserted straight after it.
        static Transform AddRowBelow(Transform row)
        {
            var clone = Object.Instantiate(row.gameObject, Templates.Holder);
            clone.name = row.name + " NextGenPatchPanels";
            for (int i = clone.transform.childCount - 1; i >= 0; i--)
            {
                var child = clone.transform.GetChild(i);
                if (child.GetComponent<ShopItem>() != null) Object.DestroyImmediate(child.gameObject);
            }
            clone.transform.SetParent(row.parent, false);
            clone.transform.SetSiblingIndex(row.GetSiblingIndex() + 1);
            return clone.transform;
        }

        static Card CardOf(ShopItem item)
        {
            var guid = item == null ? null : item.guid;
            return guid != null && guid.StartsWith(GuidPrefix) && Cards.TryGetValue(guid.Substring(GuidPrefix.Length), out var c) ? c : null;
        }

        // Postfix on ShopItem.Start / UpdateVisualState: the game fills the name from the vanilla card's
        // text, so put ours in its first line.
        public static void RefreshTextPostfix(ShopItem __instance)
        {
            try
            {
                var c = CardOf(__instance);
                if (c == null || __instance.txtName == null) return;
                var vanillaText = c.Vanilla?.txtName?.text ?? "";
                if (c.Vanilla != null && TextLogged.Add(c.Type))
                    NextGenPatchPanelsMod.Log.Msg($"Vanilla {c.Type} card text: name='{vanillaText.Replace("\n", "\\n")}' price='{c.Vanilla.txtPrice?.text}' xp='{c.Vanilla.txtXpToUnlock?.text}'");
                var lines = vanillaText.Split('\n');
                lines[0] = c.Label;
                __instance.txtName.text = string.Join("\n", lines);
                __instance.itemDisplayName = c.Label; // the cart shows this, like vanilla's first line
            }
            catch (Exception e) { NextGenPatchPanelsMod.Log.Error($"RefreshTextPostfix: {e.Message}"); }
        }
    }
}
