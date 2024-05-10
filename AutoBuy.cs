using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Ext.AutoKit;
using Oxide.Ext.AutoKit.Messages;
using Oxide.Ext.AutoKit.Models;
using Oxide.Ext.AutoKit.Settings;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("AutoBuy", "kwamaking", "2.1.0")]
    [Description("Automatically purchase a saved inventory from the shop.")]
    class AutoBuy : RustPlugin
    {
        [PluginReference] Plugin GUIShop;

        private const string UsePermission = "autobuy.use";
        private const string AutoBuyUI = "AutoBuyUI";
        private const string AutoBuyTotalCostUI = "AutoBuyTotalCostUI";
        private const string AutoBuyLoot = "AutoBuy";
        private const int MainSlots = 24;
        private const int WearSlots = 7;
        private const int BeltSlots = 6;
        private const string ButtonColor = "0.415 0.5 0.258 0.6";
        private const string ButtonTextColor = "0.607 0.705 0.431";
        private const string CloseButtonColor = "9.98 0.31 0.31 0.75";
        private const string TextColor = "0.77 0.7 0.7 1";
        private const string DefaultAnchorMin = "0.75 0.82";
        private const string DefaultAnchorMax = "0.95 0.98";
        private const string ShopConfig = "oxide/config/GUIShop.json";
        private AutoKit<ShopItem> autoKit { get; set; }
        private AutoBuyMessages messages { get; set; }
        private ShopData shopData { get; set; }
        private List<PlayerInventory.Type> playerInventoryTypes { get; set; }
        private List<LootableCorpse> corpses { get; set; } = new List<LootableCorpse>();
        private Dictionary<ulong, string> playerUIKitName { get; set; } = new Dictionary<ulong, string>();
        private List<AutoBuyKitConfiguration> kitConfigurations { get; set; }
        private List<AutoBuyKitConfiguration> incompleteKitConfigurations { get; set; } = new List<AutoBuyKitConfiguration>();
        private List<ulong> playersWithUIOpen { get; set; } = new List<ulong>();
        private Dictionary<ulong, Dictionary<PlayerInventory.Type, ItemContainer>> playerContainers { get; set; } = new Dictionary<ulong, Dictionary<PlayerInventory.Type, ItemContainer>>();
        private Dictionary<ulong, Timer> playerContainerRefreshers { get; set; } = new Dictionary<ulong, Timer>();

        #region Oxide Hooks

        void Init()
        {
            playerInventoryTypes = new List<PlayerInventory.Type> { PlayerInventory.Type.Belt, PlayerInventory.Type.Main, PlayerInventory.Type.Wear };
            permission.RegisterPermission(UsePermission, this);
            cmd.AddChatCommand("ab", this, "AutoBuyCommand");
            messages = new AutoBuyMessages();
            autoKit = new AutoKit<ShopItem>(messages, new AutoKitSettings(pluginName: this.Name, iconId: 76561198955675901));
            try
            {
                kitConfigurations = LoadKitConfigurations();
                shopData = LoadShopData();
            }
            catch (Exception e)
            {
                Puts($"Failed to load Shops Configuration, using default... {e.Message}");
                shopData = new ShopData();
                kitConfigurations = new List<AutoBuyKitConfiguration>();
            }
        }

        void OnServerSave()
        {
            autoKit.Save();
            SaveAutoBuyKitConfigurations();
        }

        void Unload()
        {
            autoKit.Save();
            SaveAutoBuyKitConfigurations();
            playerContainerRefreshers.Values.ToList().ForEach(t => t.Destroy());
        }

        private void OnLootEntityEnd(BasePlayer player, LootableCorpse corpse)
        {
            if (!corpses.Contains(corpse)) return;

            corpses.Remove(corpse);

            if (corpse != null)
                corpse.Kill();

            DestroyUI(player);
            MoveItemsBackToInventory(player, () => { });
            CleanUpIncompleteKitConfigurationsAndContainers(player);
        }

        private void OnPlayerRespawned(BasePlayer player)
        {
            timer.Once(1f, () =>
             {
                 if (!kitConfigurations.Any(p => p.playerId == player.userID)) return;
                 kitConfigurations.FindAll(p => p.playerId == player.userID && p.redeemOnSpawn).ForEach(k => autoKit.With(player, (action) =>
                  {
                      action.WithKit(k.kitName).MaybeApply(Apply);
                      Interface.CallHook("OnKitRedeemed", player, k.kitName);

                  }));
             });
        }

        private ItemContainer.CanAcceptResult? CanAcceptItem(ItemContainer container, Item item, int position)
        {
            if (position == -1 && container.itemList.Any(i => i.info.itemid == item.info.itemid && i.IsLocked())) return ItemContainer.CanAcceptResult.CannotAccept;
            if (container.GetSlot(position)?.IsLocked() ?? false) return ItemContainer.CanAcceptResult.CannotAccept;

            return null;
        }

        private Item OnItemSplit(Item item, int amount)
        {
            if (item.IsLocked()) return item;

            return null;
        }

        object CanMoveItem(Item item, PlayerInventory playerLoot, uint targetContainer, int targetSlot, int amount)
        {
            if (item.IsLocked() && item.parent != playerLoot.FindContainer(targetContainer)) return false;

            return null;
        }

        void OnEntityKill(LootableCorpse corpse)
        {
            if (corpses.Contains(corpse))
            {
                playersWithUIOpen.ToList().ForEach(p =>
                {
                    var player = Player.FindById(p);
                    if (player?.inventory?.loot?.entitySource == corpse)
                    {
                        MoveItemsBackToInventory(player, () => { });
                        player.EndLooting();
                        CleanUpIncompleteKitConfigurationsAndContainers(player);
                        DestroyUI(player);
                    }
                });
            }
        }
        #endregion

        #region Commands

        [ChatCommand("autobuy")]
        void AutoBuyCommand(BasePlayer player, string command, string[] args)
        {
            try
            {
                if (!permission.UserHasPermission(player.UserIDString, UsePermission))
                {
                    autoKit.With(player, (action) => action.ToNonDestructive().WithNotification(messages.noPermission).ToNotify().Notify());
                    return;
                }
                var run = args.ElementAtOrDefault(0) ?? "gui";
                var kitName = args.ElementAtOrDefault(1) ?? run;
                var kitConfiguration = new AutoBuyKitConfiguration();
                incompleteKitConfigurations.Add(kitConfiguration);
                switch (run)
                {
                    case "save":
                        autoKit.With(player, (action) => action.WithNewKit(kitName).Save(Save).Notify());
                        autoKit.Save();
                        break;
                    case "help":
                        autoKit.With(player, (action) => action.ToNonDestructive().WithNotification(messages.help).ToNotify().Notify());
                        break;
                    case "list":
                        autoKit.With(player, (action) => action.ToNonDestructive().ListToNotify().Notify());
                        break;
                    case "remove":
                        autoKit.With(player, (action) =>
                        {
                            action.WithKit(kitName).Remove().Notify();
                            kitConfiguration = kitConfigurations.Find(c => c.playerId == player.userID && c.kitName == kitName);
                            kitConfigurations.Remove(kitConfiguration);
                        });
                        break;
                    case "edit":
                        autoKit.With(player, (action) => action.WithKit(kitName).Apply(Edit).Notify());
                        break;
                    case "gui":
                        CreateLootMenu(player, new Kit<ShopItem>(), kitConfiguration);
                        CreateTotalCostUI(player, kitConfiguration);
                        CreateUI(player, kitConfiguration);
                        break;
                    default:
                        autoKit.With(player, (action) => action.WithKit(kitName).Apply(Apply).Notify());
                        break;
                }
            }
            catch (Exception e)
            {
                Puts($"Failed to run AutoBuy: {e.Message}, {e.StackTrace}");
            }
        }

        [ConsoleCommand("autobuy.kitname"), Permission(UsePermission)]
        void KitNameCommand(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();

            if (arg.Args.Length <= 0)
            {
                if (playerUIKitName.ContainsKey(player.userID))
                    playerUIKitName.Remove(player.userID);

                return;
            }

            if (playerUIKitName.ContainsKey(player.userID))
                playerUIKitName[player.userID] = arg.Args[0];
            else
                playerUIKitName.Add(player.userID, arg.Args[0]);
        }

        [ConsoleCommand("autobuy.uisave"), Permission(UsePermission)]
        void UISaveCommand(ConsoleSystem.Arg arg)
        {
            var kitGuid = Guid.Parse(arg.Args.ElementAtOrDefault(0));
            var kitConfiguration = FindIncompleteKitConfigurationByGuid(kitGuid);
            BasePlayer player = arg.Player();

            if (playerUIKitName.ContainsKey(player.userID))
                kitConfiguration.kitName = playerUIKitName[player.userID];

            if (null != kitConfiguration.kitName)
            {
                autoKit.With(player, (action) => action.WithKit(kitConfiguration.kitName).MaybeRemove());
                Interface.CallHook("OnKitRemoved", player, kitConfiguration.kitName);
                autoKit.With(player, (action) =>
                {
                    action.WithNewKit(kitConfiguration.kitName).MaybeSave(SaveFromUI).Notify();
                    FinalizeSaveFromUI(player, kitConfiguration);
                });
            }
        }

        [ConsoleCommand("autobuy.uiremove"), Permission(UsePermission)]
        void UIRemoveCommand(ConsoleSystem.Arg arg)
        {
            var kitGuid = Guid.Parse(arg.Args.ElementAtOrDefault(0));
            var kitConfiguration = FindIncompleteKitConfigurationByGuid(kitGuid);
            BasePlayer player = arg.Player();
            autoKit.With(player, (action) => action.WithKit(kitConfiguration.kitName).Remove().Notify());
            MoveItemsBackToInventory(player, () =>
            {
                if (kitConfiguration.saveSkinsAndLoadout)
                    Interface.CallHook("OnKitRemoved", player, kitConfiguration.kitName);
            });
            player.EndLooting();

            CleanUpIncompleteKitConfigurationsAndContainers(player);
        }

        [ConsoleCommand("autobuy.options"), Permission(UsePermission)]
        void UIOptions(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            var kitGuid = Guid.Parse(arg.Args.ElementAtOrDefault(0));
            var playerContainers = FindPlayerContainers(player);
            var kitConfiguration = FindIncompleteKitConfigurationByGuid(kitGuid);

            kitConfiguration.saveSkinsAndLoadout = bool.Parse(arg.Args.ElementAtOrDefault(1));
            kitConfiguration.redeemOnSpawn = bool.Parse(arg.Args.ElementAtOrDefault(2));

            CreateUI(player, kitConfiguration);

        }

        [ConsoleCommand("autobuy.clearcontainers"), Permission(UsePermission)]
        void UIClearContainers(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            var kitGuid = Guid.Parse(arg.Args.ElementAtOrDefault(0));
            var playerContainers = FindPlayerContainers(player);
            MoveItemsBackToInventory(player, () => { });
            playerContainers.Keys.ToList().ForEach(c => playerContainers[c].Clear());
            var kitConfiguration = FindIncompleteKitConfigurationByGuid(kitGuid);

            CreateTotalCostUI(player, kitConfiguration);
            CreateUI(player, kitConfiguration);
        }

        #endregion

        #region AutoBuy

        private Kit<ShopItem> Save(BasePlayer player, Kit<ShopItem> kit)
        {
            playerInventoryTypes.ForEach(inventoryType =>
                player.inventory.GetContainer(inventoryType).itemList.ForEach(item =>
                      AddItemToKit(kit, item, inventoryType)
                  )
            );

            Interface.CallHook("OnKitSaved", player, kit.name);

            return kit;
        }

        private Kit<ShopItem> SaveFromUI(BasePlayer player, Kit<ShopItem> kit)
        {
            playerContainers[player.userID].Keys.ToList().ForEach(inventoryType =>
                 playerContainers[player.userID][inventoryType].itemList.ForEach(item =>
                      AddItemToKit(kit, item, inventoryType)
                 )
            );
            return kit;
        }

        private void Apply(BasePlayer player, Kit<ShopItem> kit)
        {
            if (null == kit) return;

            kit.items.ForEach(item =>
            {
                if (!string.IsNullOrEmpty(item.shop))
                {
                    GUIShop?.Call("TryShopBuy", player, item.shop, item.name, item.amount);
                }
                item.attachments?.ForEach(attachment =>
                {
                    if (!string.IsNullOrEmpty(attachment.shop))
                    {
                        GUIShop?.Call("TryShopBuy", player, attachment.shop, attachment.name, attachment.amount);
                    }
                });

            });

            Interface.CallHook("OnKitRedeemed", player, kit.name);
        }

        private void Edit(BasePlayer player, Kit<ShopItem> kit)
        {
            if (null == kit) return;

            var kitConfiguration = kitConfigurations
            .Find(c => c.playerId == player.userID && c.kitName == kit.name) ?? new AutoBuyKitConfiguration { kitName = kit.name, playerId = player.userID };

            incompleteKitConfigurations.Add(kitConfiguration);
            CreateLootMenu(player, kit, kitConfiguration);
            CreateTotalCostUI(player, kitConfiguration);
            CreateUI(player, kitConfiguration);
        }

        private void FinalizeSaveFromUI(BasePlayer player, AutoBuyKitConfiguration kitConfiguration)
        {
            var existingKitConfiguration = kitConfigurations.Find(c => c.playerId == player.userID && c.kitName == kitConfiguration.kitName);
            if (null != existingKitConfiguration)
                kitConfigurations.Remove(existingKitConfiguration);
            if (playerUIKitName.ContainsKey(player.userID))
                playerUIKitName.Remove(player.userID);

            var playerContainers = FindPlayerContainers(player);
            kitConfiguration.totalCost = CalculatePurchasePrice(playerContainers);
            kitConfiguration.playerId = player.userID;

            kitConfigurations.Add(kitConfiguration);

            MoveItemsBackToInventory(player, () =>
            {
                if (kitConfiguration.saveSkinsAndLoadout)
                    Interface.CallHook("OnKitSaved", player, kitConfiguration.kitName);
            });

            player.EndLooting();
            CleanUpIncompleteKitConfigurationsAndContainers(player);
        }

        private void AddItemToKit(Kit<ShopItem> kit, Item item, PlayerInventory.Type inventoryType)
        {
            kit.items.Add(new ShopItem
            {
                id = item.info.itemid,
                name = item.info.displayName.english,
                amount = item.amount,
                shop = FindItemShopCategory(item.info.displayName.english),
                position = item.position,
                skinId = item.skin,
                inventoryType = inventoryType,
                attachments = item.contents?.itemList?.ConvertAll(attachment =>
                {
                    return new ShopItem
                    {
                        name = attachment.info.displayName.english,
                        id = attachment.info.itemid,
                        shop = FindItemShopCategory(attachment.info.displayName.english),
                    };
                }) ?? new List<ShopItem>()
            });
        }

        private void RefreshPlayerContainers()
        {
            try
            {
                playersWithUIOpen.ToList().ForEach(p =>
                {
                    var player = Player.FindById(p);
                    incompleteKitConfigurations.ForEach(k =>
                    {
                        var playerContainers = FindPlayerContainers(player);
                        if (null != playerContainers)
                        {
                            k.totalCost = CalculatePurchasePrice(playerContainers);
                            CreateTotalCostUI(player, k);
                        }
                        else
                            DestroyUI(player);
                    });
                    if (incompleteKitConfigurations.IsEmpty())
                        DestroyUI(player);
                    corpses.ForEach(c =>
                    {
                        if (c.IsDestroyed)
                            DestroyUI(player);
                    });
                });
            }
            catch (Exception) { }
        }

        private string FindItemShopCategory(string item)
        {
            return shopData.shopCategories.Keys.ToList().Where(k => shopData.shopCategories[k].items.Any(i => i == item)).FirstOrDefault();
        }

        private double CalculatePurchasePrice(List<ItemContainer> containers)
        {
            var totalPrice = 0.0;
            containers?.ForEach(c =>
                c.itemList.ForEach(i =>
                    shopData.shopItems.Keys.ToList()
                    .Where(k => k == i.info.displayName.english).ToList().ForEach(m =>
                    {
                        totalPrice += (shopData.shopItems[m].price * i.amount);
                        totalPrice += null != i.contents ? CalculatePurchasePrice(new List<ItemContainer> { i.contents }) : 0.0;
                    })
                )
            );

            return totalPrice;
        }

        private double CalculatePurchasePrice(Dictionary<PlayerInventory.Type, ItemContainer> containers)
        {
            return CalculatePurchasePrice(containers?.Keys.ToList().Select(k => containers[k]).ToList());
        }

        private bool OnlyClothing(Item item, int amount)
        {
            return item.info.isWearable;
        }

        private void MoveItemsBackToInventory(BasePlayer player, Action inventorySnapshot)
        {
            var mainContainer = player.inventory.GetContainer(PlayerInventory.Type.Main);
            var lockedItems = new List<Item>();
            var existingItems = new List<Item>();
            playerContainers[player.userID].Keys.ToList().ForEach(inventoryType =>
            {
                var playerInventoryContainer = player.inventory.GetContainer(inventoryType);
                playerContainers[player.userID][inventoryType].itemList.ToList().ForEach(item =>
                {
                    var existingItem = playerInventoryContainer.GetSlot(item.position);
                    if (item.IsLocked())
                    {
                        lockedItems.Add(item);
                    }
                    if (existingItem != null)
                    {
                        existingItems.Add(existingItem);
                        existingItem.RemoveFromContainer();
                    }

                    if (!item.MoveToContainer(playerInventoryContainer, item.position))
                        item.MoveToContainer(mainContainer);
                });
            });

            inventorySnapshot();

            lockedItems.ForEach(i =>
            {
                i.RemoveFromContainer();
                i.RemoveFromWorld();
                i.Remove();
                i.MarkDirty();
            });

            existingItems.ForEach(i =>
            {
                if (!i.MoveToContainer(mainContainer))
                    i.DropAndTossUpwards(player.GetDropPosition());
            });
        }

        private void FillContainersWithExistingKit(BasePlayer player, Kit<ShopItem> kit, Dictionary<PlayerInventory.Type, ItemContainer> containers)
        {
            kit.items?.ForEach(item =>
            {
                var lockedItem = ItemManager.CreateByItemID(item.id, item.amount, item.skinId);
                lockedItem?.MoveToContainer(containers[item.inventoryType], item.position);
                item.attachments.ForEach(a =>
                {
                    var lockedAttachment = ItemManager.CreateByItemID(a.id, a.amount);
                    lockedItem?.contents?.AddItem(lockedAttachment.info, 1);
                });
                lockedItem?.contents?.SetLocked(true);
                lockedItem?.LockUnlock(true);
            });
        }

        private AutoBuyKitConfiguration FindIncompleteKitConfigurationByGuid(Guid guid)
        {
            return incompleteKitConfigurations.Find(k => k.guid == guid);
        }

        private void CleanUpIncompleteKitConfigurationsAndContainers(BasePlayer player)
        {
            incompleteKitConfigurations.ToList().Where(k => k.playerId == player.userID).ToList().ForEach(k => incompleteKitConfigurations.Remove(k));
            if (playerContainers.ContainsKey(player.userID))
            {
                playerContainers[player.userID].Keys.ToList().ForEach(k => playerContainers[player.userID][k].Clear());
                playerContainers.Remove(player.userID);
            }
            if (playerContainerRefreshers.ContainsKey(player.userID))
            {
                playerContainerRefreshers[player.userID].Destroy();
                playerContainerRefreshers.Remove(player.userID);
            }
            if (playerUIKitName.ContainsKey(player.userID))
                playerUIKitName.Remove(player.userID);

            DestroyUI(player);
        }

        private Dictionary<PlayerInventory.Type, ItemContainer> FindPlayerContainers(BasePlayer player)
        {
            Dictionary<PlayerInventory.Type, ItemContainer> autoBuyContainers;
            playerContainers.TryGetValue(player.userID, out autoBuyContainers);

            return autoBuyContainers;
        }

        #endregion

        #region File IO

        public void SaveAutoBuyKitConfigurations()
        {
            try
            {
                Interface.Oxide.DataFileSystem.WriteObject("AutoBuyKitConfigurations", kitConfigurations);
            }
            catch (Exception e)
            {
                Puts($"Failed to save auto buy kit configurations {e.Message} {e.StackTrace}");
            }
        }

        private List<AutoBuyKitConfiguration> LoadKitConfigurations()
        {
            try
            {
                return Interface.Oxide.DataFileSystem.ReadObject<List<AutoBuyKitConfiguration>>("AutoBuyKitConfigurations");
            }
            catch (Exception e)
            {
                Puts($"Failed to load auto buy kit configurations:  {e.Message} {e.StackTrace}");
                return new List<AutoBuyKitConfiguration>();
            }
        }

        private ShopData LoadShopData()
        {
            try
            {
                return Interface.Oxide.DataFileSystem.GetDatafile(ShopConfig).ReadObject<ShopData>(ShopConfig);
            }
            catch (Exception e)
            {
                Puts($"Failed to load shop data:  {e.Message} {e.StackTrace}");
                return new ShopData();
            }
        }

        #endregion

        #region UI

        private void CreateTotalCostUI(BasePlayer player, AutoBuyKitConfiguration kitConfiguration)
        {
            CuiHelper.DestroyUi(player, AutoBuyTotalCostUI);
            CuiElementContainer result = new CuiElementContainer();
            string rootPanel = result.Add(new CuiPanel
            {
                Image = new CuiImageComponent
                {
                    Color = "0 0 0 0.4"
                },
                RectTransform =
                {
                    AnchorMin = "0.75 0.82",
                    AnchorMax = "0.95 0.85"
                }
            }, "Hud.Menu", AutoBuyTotalCostUI);

            string contentPanel = result.Add(new CuiPanel
            {
                Image = new CuiImageComponent
                {
                    Color = "0 0 0 0"
                },
                RectTransform =
                {
                    AnchorMin = "0.02 0.1",
                    AnchorMax = "0.95 0.97"
                }
            }, rootPanel);
            result.Add(new CuiLabel
            {
                RectTransform =
                {
                    AnchorMin = "0 0",
                    AnchorMax = "0.5 1"
                },
                Text =
                {
                    Text = $"Total Kit Cost:",
                    Align = TextAnchor.LowerLeft,
                    Color = TextColor,
                    FontSize = Convert.ToInt32(14)
                }
            }, contentPanel);

            result.Add(new CuiLabel
            {
                RectTransform =
                {
                    AnchorMin = "0.55 0",
                    AnchorMax = "1 1"
                },
                Text =
                {
                    Text = $"${kitConfiguration.totalCost}",
                    Align = TextAnchor.LowerRight,
                    Color = "0.58 0.74 0.26 0.9",
                    FontSize = Convert.ToInt32(14)
                }
            }, contentPanel);

            CuiHelper.AddUi(player, result);
        }

        private void CreateUI(BasePlayer player, AutoBuyKitConfiguration kitConfiguration)
        {
            CuiHelper.DestroyUi(player, AutoBuyUI);
            playersWithUIOpen.Add(player.userID);

            CuiElementContainer result = new CuiElementContainer();
            string rootPanel = result.Add(new CuiPanel
            {
                Image = new CuiImageComponent
                {
                    Color = "0.75 0.75 0.75 0.2"
                },
                RectTransform =
                {
                    AnchorMin = DefaultAnchorMin,
                    AnchorMax = DefaultAnchorMax
                }
            }, "Hud.Menu", AutoBuyUI);

            string headerPanel = result.Add(new CuiPanel
            {
                Image = new CuiImageComponent
                {
                    Color = "0 0 0 0.5"
                },
                RectTransform =
                {
                    AnchorMin = "0 0.77",
                    AnchorMax = "1 1"
                }
            }, rootPanel);

            result.Add(new CuiLabel
            {
                RectTransform =
                {
                    AnchorMin = "0.02 0.02",
                    AnchorMax = "0.6 0.95"
                },
                Text =
                {
                    Text = $"AutoBuy",
                    Align = TextAnchor.MiddleLeft,
                    Color = "0.58 0.74 0.26 0.9",
                    FontSize = Convert.ToInt32(14)
                }
            }, headerPanel);

            string contentPanel = result.Add(new CuiPanel
            {
                Image = new CuiImageComponent
                {
                    Color = "0 0 0 0.1"
                },
                RectTransform =
                {
                    AnchorMin = "0 0",
                    AnchorMax = "1 0.74"
                }
            }, rootPanel);

            result.Add(new CuiLabel
            {
                RectTransform =
                {
                    AnchorMin = "0.02 0.78",
                    AnchorMax = "0.6 0.98"
                },
                Text =
                {
                    Text = "Save skins and loadout:",
                    Align = TextAnchor.MiddleLeft,
                    Color = TextColor,
                    FontSize = Convert.ToInt32(13)
                }
            }, contentPanel);

            result.Add(new CuiButton
            {
                RectTransform =
                {
                    AnchorMin = "0.75 0.78",
                    AnchorMax = "0.98 0.98"
                },
                Button =
                {
                    Command = $"autobuy.options {kitConfiguration.guid} {!kitConfiguration.saveSkinsAndLoadout} {kitConfiguration.redeemOnSpawn}",
                    Color = kitConfiguration.saveSkinsAndLoadout ? ButtonColor : CloseButtonColor
                },
                Text =
                {
                    Align = TextAnchor.MiddleCenter,
                    Text = kitConfiguration.saveSkinsAndLoadout ? "Yes" : "No",
                    Color = ButtonTextColor,
                    FontSize = Convert.ToInt32(11)
                }
            }, contentPanel);

            result.Add(new CuiLabel
            {
                RectTransform =
                {
                    AnchorMin = "0.02 0.56",
                    AnchorMax = "0.6 0.76"
                },
                Text =
                {
                    Text = "Spawn with this kit:",
                    Align = TextAnchor.MiddleLeft,
                    Color = TextColor,
                    FontSize = Convert.ToInt32(13)
                }
            }, contentPanel);

            result.Add(new CuiButton
            {
                RectTransform =
                {
                    AnchorMin = "0.75 0.56",
                    AnchorMax = "0.98 0.76"
                },
                Button =
                {
                    Command = $"autobuy.options {kitConfiguration.guid} {kitConfiguration.saveSkinsAndLoadout} {!kitConfiguration.redeemOnSpawn}",
                    Color = kitConfiguration.redeemOnSpawn ? ButtonColor : CloseButtonColor
                },
                Text =
                {
                    Align = TextAnchor.MiddleCenter,
                    Text = kitConfiguration.redeemOnSpawn ? "Yes" : "No",
                    Color = ButtonTextColor,
                    FontSize = Convert.ToInt32(11)
                }
            }, contentPanel);

            if (null == kitConfiguration.kitName)
            {
                var inputField = result.Add(new CuiPanel
                {
                    Image = new CuiImageComponent
                    {
                        Color = "0 0 0 0.8"
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.75 0.34",
                        AnchorMax = "0.98 0.54"
                    }
                }, contentPanel);

                result.Add(new CuiLabel
                {
                    RectTransform =
                    {
                        AnchorMin = "0.02 0.34",
                        AnchorMax = "0.4 0.54"
                    },
                    Text =
                    {
                        Text = "Kit Name:",
                        Align = TextAnchor.MiddleLeft,
                        Color = TextColor,
                        FontSize = Convert.ToInt32(13)
                    }
                }, contentPanel);

                result.Add(new CuiElement
                {
                    Name = "KitName",
                    Parent = inputField,
                    Components =
                    {
                        new CuiInputFieldComponent
                        {
                            Text = String.Empty,
                            CharsLimit = 20,
                            Color = "1 1 1 1",
                            IsPassword = false,
                            Command = "autobuy.kitname",
                            FontSize = Convert.ToInt32(13),
                            Align = TextAnchor.MiddleLeft
                        },

                        new CuiRectTransformComponent
                        {
                            AnchorMin = "0.05 0",
                            AnchorMax = "1 1"
                        }
                    }
                });
            }
            else
            {
                result.Add(new CuiLabel
                {
                    RectTransform =
                    {
                        AnchorMin = "0.02 0.34",
                        AnchorMax = "0.7 0.54"
                    },
                    Text =
                    {
                        Text = "Kit Name: ",
                        Align = TextAnchor.MiddleLeft,
                        Color = TextColor,
                        FontSize = Convert.ToInt32(13)
                    }
                }, contentPanel);

                result.Add(new CuiLabel
                {
                    RectTransform =
                    {
                        AnchorMin = "0.75 0.34",
                        AnchorMax = "0.98 0.54"
                    },
                    Text =
                    {
                        Text = kitConfiguration.kitName,
                        Align = TextAnchor.MiddleRight,
                        Color = "0.58 0.74 0.26 0.9",
                        FontSize = Convert.ToInt32(14)
                    }
                }, contentPanel);

                result.Add(new CuiButton
                {
                    RectTransform =
                    {
                        AnchorMin = "0.52 0.1",
                        AnchorMax = "0.66 0.85"
                    },
                    Button =
                    {
                        Command = $"autobuy.uiremove {kitConfiguration.guid}",
                        Color = CloseButtonColor
                    },
                    Text =
                    {
                        Align = TextAnchor.MiddleCenter,
                        Text = "Delete",
                        Color = TextColor,
                        FontSize = Convert.ToInt32(11)
                    }
                }, headerPanel);
            }

            result.Add(new CuiButton
            {
                RectTransform =
                {
                    AnchorMin = "0.84 0.1",
                    AnchorMax = "0.98 0.85"
                },
                Button =
                {
                    Command = $"autobuy.uisave {kitConfiguration.guid}",
                    Color = ButtonColor
                },
                Text =
                {
                    Align = TextAnchor.MiddleCenter,
                    Text = "Save",
                    Color = ButtonTextColor,
                    FontSize = Convert.ToInt32(11)
                }
            }, headerPanel);

            result.Add(new CuiButton
            {
                RectTransform =
                {
                    AnchorMin = "0.68 0.1",
                    AnchorMax = "0.82 0.85"
                },
                Button =
                {
                    Command = $"autobuy.clearcontainers {kitConfiguration.guid}",
                    Color = CloseButtonColor
                },
                Text =
                {
                    Align = TextAnchor.MiddleCenter,
                    Text = "Clear",
                    Color = TextColor,
                    FontSize = Convert.ToInt32(11)
                }
            }, headerPanel);
            CuiHelper.AddUi(player, result);
        }

        private void CreateLootMenu(BasePlayer player, Kit<ShopItem> kit, AutoBuyKitConfiguration kitConfiguration)
        {
            if (!playerContainerRefreshers.ContainsKey(player.userID))
                playerContainerRefreshers.Add(player.userID, timer.Every(1f, () => RefreshPlayerContainers()));

            player.EndLooting();
            LootableCorpse corpse = GameManager.server.CreateEntity(StringPool.Get(2604534927), Vector3.zero) as LootableCorpse;
            corpse.CancelInvoke("RemoveCorpse");
            corpse.syncPosition = false;
            corpse.limitNetworking = true;
            corpse.playerName = null == kitConfiguration.kitName ? AutoBuyLoot : kitConfiguration.kitName;
            corpse.playerSteamID = 0;
            corpse.enableSaving = false;
            corpse.Spawn();
            corpse.SendAsSnapshot(player.Connection);

            var inventory = new ItemContainer
            {
                entityOwner = player,
                capacity = MainSlots,
                allowedContents = ItemContainer.ContentsType.Generic,
                isServer = true
            };

            var clothing = new ItemContainer
            {
                entityOwner = player,
                capacity = WearSlots,
                allowedContents = ItemContainer.ContentsType.Generic,
                isServer = true,
                canAcceptItem = OnlyClothing
            };

            var belt = new ItemContainer
            {
                entityOwner = player,
                capacity = BeltSlots,
                allowedContents = ItemContainer.ContentsType.Generic,
                isServer = true
            };

            inventory.GiveUID();
            clothing.GiveUID();
            belt.GiveUID();

            if (playerContainers.ContainsKey(player.userID))
                playerContainers.Remove(player.userID);

            playerContainers.Add(player.userID, new Dictionary<PlayerInventory.Type, ItemContainer>
            {
                { PlayerInventory.Type.Main, inventory },
                { PlayerInventory.Type.Wear, clothing },
                { PlayerInventory.Type.Belt, belt }
            });

            FillContainersWithExistingKit(player, kit, playerContainers[player.userID]);

            corpses.Add(corpse);

            timer.Once(0.3f, () =>
            {
                player.inventory.loot.Clear();
                player.inventory.loot.AddContainer(inventory);
                player.inventory.loot.AddContainer(clothing);
                player.inventory.loot.AddContainer(belt);
                player.inventory.loot.PositionChecks = false;
                player.inventory.loot.entitySource = corpse;
                player.inventory.loot.itemSource = null;
                player.inventory.loot.MarkDirty();
                player.inventory.loot.SendImmediate();
                player.ClientRPCPlayer(null, player, "RPC_OpenLootPanel", "player_corpse");
                player.SendNetworkUpdateImmediate();
            });
        }

        private void DestroyUI(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, AutoBuyTotalCostUI);
            CuiHelper.DestroyUi(player, AutoBuyUI);

            playersWithUIOpen.Remove(player.userID);
        }

        #endregion

        #region Configuration Classes

        public class ShopItem
        {
            [JsonProperty("id")]
            public int id { get; set; }
            [JsonProperty("name")]
            public string name { get; set; }
            [JsonProperty("shop")]
            public string shop { get; set; }
            [JsonProperty("amount")]
            public int amount { get; set; } = 1;
            [JsonProperty("attachments")]
            public List<ShopItem> attachments { get; set; } = new List<ShopItem>();
            [JsonProperty("position")]
            public int position { get; set; } = -1;
            [JsonProperty("skinId")]
            public ulong skinId { get; set; } = 0;
            [JsonProperty("inventoryType")]
            public PlayerInventory.Type inventoryType { get; set; } = PlayerInventory.Type.Main;
        }

        public class AutoBuyKitConfiguration
        {
            [JsonProperty("guid")]
            public Guid guid { get; set; } = Guid.NewGuid();
            [JsonProperty("playerId")]
            public ulong playerId { get; set; }
            [JsonProperty("kitName")]
            public string kitName { get; set; }
            [JsonProperty("redeemOnSpawn")]
            public bool redeemOnSpawn { get; set; } = false;
            [JsonProperty("saveSkinsAndLoadout")]
            public bool saveSkinsAndLoadout { get; set; } = false;
            [JsonProperty("totalCost")]
            public double totalCost { get; set; } = 0;
        }

        public class ShopData
        {
            [JsonProperty(Required = Required.Always, PropertyName = "Shop - Shop Categories")]
            public Dictionary<string, ShopItemData> shopItems { get; set; } = new Dictionary<string, ShopItemData>();
            [JsonProperty(Required = Required.Always, PropertyName = "Shop - Shop List")]
            public Dictionary<string, ShopCategoryData> shopCategories { get; set; } = new Dictionary<string, ShopCategoryData>();
        }

        public class ShopItemData
        {
            [JsonProperty(Required = Required.Always, PropertyName = "buy")]
            public double price { get; set; } = 0;
        }

        public class ShopCategoryData
        {
            [JsonProperty(Required = Required.Always, PropertyName = "buy")]
            public List<string> items { get; set; } = new List<string>();
        }

        public class AutoBuyMessages : AutoKitMessages
        {
            public string help { get; set; } =
                "\n<color=green>/autobuy</color>. - Opens the AutoBuy UI, \n" +
                "<color=green>/autobuy edit <kit></color>. - Edit an existing kit in the AutoBuy UI, \n" +
                "<color=green>/autobuy <kit></color>. - Apply a saved buy kit to your inventory, \n" +
                "<color=green>/autobuy save <kit></color> - Save the items in your inventory as a buy kit, \n" +
                "<color=green>/autobuy list</color> - List your saved buy kits, \n" +
                "<color=green>/autobuy remove <kit></color> - Remove a saved buy kit, \n" +
                "<color=green>/autobuy help</color> - To see this message again.\n" +
                "<color=green>/ab</color> Command shortcut.\n ";
            public string noPermission { get; set; } = "You do not have permission to use AutoBuy.";
        }
        #endregion
    }
}
