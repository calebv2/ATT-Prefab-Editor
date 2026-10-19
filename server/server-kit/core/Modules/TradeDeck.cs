using System;
using System.Reflection;
using Alta.Console;
using Alta.Networking;
using Alta.Inventory;
using Alta.Trading;
using UnityEngine;
using Newtonsoft.Json;

namespace PrefabEditor.Modules
{
    public static partial class EditorAdminModule
    {
        static T TradeField<T>(TradeVendor vendor, string name)
        {
            FieldInfo field = typeof(TradeVendor).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field == null) throw new Exception("unsupported game build: missing trade field " + name);
            return (T)field.GetValue(vendor);
        }
        static string ResolveTrade(uint id, out NetworkEntity root, out TradeVendor vendor)
        {
            vendor = null;
            string error = ResolveRoot(id, out root);
            if (error != null) return error;
            TradeVendor[] vendors = root.GetComponentsInChildren<TradeVendor>(true);
            if (vendors.Length != 1) return "select one trade deck (prefab 30016); entity #" + id + " contains " + vendors.Length + " trade vendors";
            vendor = vendors[0];
            return null;
        }
        static string TradeRecord(NetworkEntity root, TradeVendor vendor)
        {
            PickupDock stock = TradeField<PickupDock>(vendor, "stockDock");
            ButtonCounter coins = TradeField<ButtonCounter>(vendor, "coinCounter");
            ButtonCounter items = TradeField<ButtonCounter>(vendor, "itemCounter");
            Pickup pickup = stock.DockedPickup;
            NetworkPrefab prefab = pickup == null ? null : pickup.Entity.Prefab;
            return "{\"ok\":true,\"entity\":" + EntityRecord(root, root.transform.position)
                + ",\"trade\":" + JsonConvert.SerializeObject(new {
                    itemHash = prefab == null ? 0u : prefab.Hash,
                    itemName = prefab == null ? "" : prefab.name,
                    price = coins.Amount, perPurchase = items.Amount, stock = stock.QuantityStored,
                    owner = vendor.Owner, state = vendor.TradeState.ToString(),
                    priceMax = coins.Maximum, quantityMax = items.Maximum
                }) + "}";
        }
        [Command("tradeinfo", "Read selected trade deck: edit tradeinfo <entity-id>")]
        public static string TradeInfo(uint id)
        {
            try {
                NetworkEntity root; TradeVendor vendor;
                string error = ResolveTrade(id, out root, out vendor);
                return error == null ? TradeRecord(root, vendor) : EditJson.Err(error);
            } catch (Exception e) { return EditJson.Err("trade deck: " + e.Message); }
        }
        internal static string ValidateTradeValues(uint hash, int price, int perPurchase, int stock)
        {
            if (hash == 0) return "choose an item from the list or enter its prefab ID";
            if (price < 1 || price > 1000000) return "price must be 1 to 1000000 coins per purchase";
            if (perPurchase < 1) return "items per purchase must be a positive whole number";
            if (stock < perPurchase) return "total stock must cover one purchase";
            return null;
        }
        [Command("tradeaddstock", "Add stock without changing price: edit tradeaddstock <entity-id> <item-hash> <amount>")]
        public static string TradeAddStock(uint id, uint hash, int amount)
        {
            try {
                if (amount < 1) return EditJson.Err("enter a positive whole number of items to add");
                NetworkEntity root; TradeVendor vendor;
                string error = ResolveTrade(id, out root, out vendor);
                if (error != null) return EditJson.Err(error);
                PickupDock dock = TradeField<PickupDock>(vendor, "stockDock");
                if (!dock.HasDockedPickup || dock.DockedPickup.Entity.Prefab == null)
                    return EditJson.Err("set the item and starting stock with Apply first; this deck has no stock to add to");
                if (dock.DockedPickup.Entity.Prefab.Hash != hash)
                    return EditJson.Err("the deck's item has changed; reload its settings before adding stock");
                long total = (long)dock.QuantityStored + amount;
                if (total > int.MaxValue) return EditJson.Err("that amount exceeds the game's stock number limit");
                // Read the current server quantities here, not an old client snapshot.
                return TradeSet(id, hash, TradeField<ButtonCounter>(vendor, "coinCounter").Amount,
                    TradeField<ButtonCounter>(vendor, "itemCounter").Amount, (int)total);
            } catch (Exception e) { return EditJson.Err("add stock: " + e.Message); }
        }

        [Command("tradeset", "Set trade deck: edit tradeset <entity-id> <item-hash> <coins> <items-per-purchase> <total-stock>")]
        public static string TradeSet(uint id, uint hash, int price, int perPurchase, int stockCount)
        {
            NetworkEntity created = null;
            PickupDock dock = null;
            ButtonCounter coins = null, items = null;
            int oldStock = 0, oldCoins = 0, oldItems = 0;
            bool changed = false, captured = false, suppliedNewItem = false;
            try
            {
                string error = ValidateTradeValues(hash, price, perPurchase, stockCount);
                if (error != null) return EditJson.Err(error);
                NetworkEntity root; TradeVendor vendor;
                error = ResolveTrade(id, out root, out vendor);
                if (error != null) return EditJson.Err(error);
                if (TradeField<bool>(vendor, "isProcessingSale") || TradeField<bool>(vendor, "isEjecting"))
                    return EditJson.Err("someone is using this trade deck; wait for the sale to finish");
                dock = TradeField<PickupDock>(vendor, "stockDock");
                coins = TradeField<ButtonCounter>(vendor, "coinCounter");
                items = TradeField<ButtonCounter>(vendor, "itemCounter");
                if (coins.ActivePlayer != null || items.ActivePlayer != null)
                    return EditJson.Err("release the trade deck's price/quantity buttons before applying changes");
                if (price < coins.Minimum || price > coins.Maximum || perPurchase < items.Minimum || perPurchase > items.Maximum)
                    return EditJson.Err("this deck supports price " + Math.Max(1, coins.Minimum) + "-" + coins.Maximum
                        + " and quantity " + Math.Max(1, items.Minimum) + "-" + items.Maximum);
                Pickup pickup = dock.DockedPickup;
                oldStock = dock.QuantityStored; oldCoins = coins.Amount; oldItems = items.Amount; captured = true;
                if (pickup != null && (pickup.Entity.Prefab == null || pickup.Entity.Prefab.Hash != hash))
                    return EditJson.Err("remove the current stock from the deck in-game before choosing a different item; your existing items have not been changed");
                if (pickup == null)
                {
                    PrefabManager.PrepareSpawnSetups();
                    NetworkPrefab prefab = PrefabManager.GetPrefab(hash);
                    if (prefab == null || !prefab.CanBeSpawnedThroughCommand) return EditJson.Err("item prefab " + hash + " is not spawnable on this server");
                    created = SpawnHelper.Spawn(prefab, SpawnData.Default, null, root.transform.position + Vector3.up, Quaternion.identity);
                    if (created == null) return EditJson.Err("could not create the trade item");
                    suppliedNewItem = true;
                    pickup = created.CommonPickup;
                    if (pickup == null || pickup.Item == null || pickup.Item == CommonPrefabs.PouchItem)
                        throw new Exception("choose a sellable item, not a structure or empty pouch");
                }
                if (!pickup.Item.IsStackable && perPurchase != 1)
                    throw new Exception("this item is not stackable; use 1 item per purchase");
                if (created != null)
                {
                    // Console-authorized editor action bypasses only the owner's dock
                    // restriction, for this synchronous operation. Native item filters stay.
                    MethodInfo filterMethod = typeof(TradeVendor).GetMethod("FilterWrongOwnerDock", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (filterMethod == null) throw new Exception("unsupported trade ownership filter");
                    DockFilter ownerFilter = (DockFilter)Delegate.CreateDelegate(typeof(DockFilter), vendor, filterMethod);
                    dock.DockFilter -= ownerFilter;
                    try {
                        if ((int)pickup.DockInto(null, dock, 1, true) != 0) throw new Exception("the trade deck cannot accept this item");
                    } finally { dock.DockFilter += ownerFilter; }
                    if (!dock.HasDockedPickup) throw new Exception("the item did not enter the trade deck");
                }
                if (!dock.CanDockAmount(stockCount - dock.QuantityStored)) throw new Exception("total stock exceeds this deck's capacity");
                changed = true;
                dock.QuantityStored = stockCount;
                coins.ForceAmount(price);
                items.ForceAmount(perPurchase);
                if (coins.Amount != price || items.Amount != perPurchase || dock.QuantityStored != stockCount)
                    throw new Exception("the game did not accept the requested price or quantity");
                string response = TradeRecord(root, vendor);
                created = null; // stock now belongs to the deck and native world save
                return response;
            }
            catch (Exception e)
            {
                string rollback = "";
                try {
                    if (suppliedNewItem) {
                        // Native docking may replace/destroy the original spawned entity.
                        // The dock was empty before this synchronous editor operation.
                        if (dock != null && dock.HasDockedPickup) dock.DestroyAmountToEmpty();
                        if (created != null && !created.IsBeingDestroyed) created.SceneDestroy();
                    } else if (changed && dock != null) dock.QuantityStored = oldStock;
                    if (captured && coins != null) coins.ForceAmount(oldCoins);
                    if (captured && items != null) items.ForceAmount(oldItems);
                } catch (Exception restore) { rollback = "; recovery failed: " + restore.Message + ". Reload the deck settings before retrying."; }
                return EditJson.Err("trade deck: " + e.Message + rollback);
            }
        }
    }
}
