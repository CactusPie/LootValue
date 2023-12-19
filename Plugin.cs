using System;
using System.Reflection;
using EFT;
using EFT.UI.DragAndDrop;
using EFT.InventoryLogic;
using EFT.UI;
using Aki.Reflection.Patching;
using Aki.Reflection.Utils;
using CurrencyUtil = GClass2334;
using static LootValue.Globals;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.EventSystems;
using System.Collections.Generic;
using Newtonsoft.Json;
using static UnityEngine.EventSystems.EventTrigger;
using static System.Collections.Specialized.BitVector32;
using System.Threading.Tasks;

namespace LootValue
{
    [BepInPlugin(pluginGuid, pluginName, pluginVersion)]
    public class LootValueMod : BaseUnityPlugin
    {
        // BepinEx
        public const string pluginGuid = "IhanaMies.LootValue";
        public const string pluginName = "LootValue";
        public const string pluginVersion = "1.2.2";

		private void Awake()
		{
			Config.SaveOnConfigSet = true;

			logger = Logger;

			SetupConfig();

			new TraderPatch().Enable();
			new ShowTooltipPatch().Enable();
			new GridItemOnPointerEnterPatch().Enable();
			new GridItemOnPointerExitPatch().Enable();
			new ItemViewOnClickPatch().Enable();

			Config.SettingChanged += Config_SettingChanged;
		}

		private void Config_SettingChanged(object sender, SettingChangedEventArgs e)
		{
			ConfigEntryBase entry = e.ChangedSetting;

			logger.LogInfo($"Settings changed - {entry.Definition.Section}:{entry.Definition.Key}");

			if (entry.Definition.Key == "Custom colours")
			{
				if (UseCustomColours.Value)
				{
					logger.LogInfo($"Read colors");
					SlotColoring.ReadColors(CustomColours.Value);
				}
			}

			if (entry.Definition.Key == "Custom colours" || entry.Definition.Key == "Use custom colours")
			{
				if (UseCustomColours.Value)
				{
					SlotColoring.ReadColors(CustomColours.Value);
				}
				else
				{
					SlotColoring.UseDefaultColors();
				}
			}
		}

		internal static ConfigEntry<bool> UseCustomColours;
		internal static ConfigEntry<string> CustomColours;
		internal static ConfigEntry<bool> EnableQuickSell;
		internal static ConfigEntry<bool> OnlyShowTotalValue;

		private void SetupConfig()
		{
			OnlyShowTotalValue = Config.Bind("Quick Sell", "Only show total value", false);
			EnableQuickSell = Config.Bind("Quick Sell", "Enable quick sell", true, "Hold Left Alt + Left Shift while left clicking an item to quick sell either to flea (if enabled) or trader which ever has better value");

			UseCustomColours = Config.Bind("Colours", "Use custom colours", false);
			CustomColours = Config.Bind("Colours", "Custom colours", "[5000:#ff0000],[10000:#ffff00],[:#ffffff]",
@"Colouring bound is marked as [int:hexcolor] e.q. [lower than this value : will be this hexcolor]
The values should incremental from lower to higher and last value should be valueless.
For example [5000:#ff0000],[10000:#ffff00],[:#ffffff] means three different bounds.
Anything under 5000 rubles, will be red.
Anything under 10000 rubles, will be yellow.
The third is marked as the ultimate color. Anything over 10000 rubles would be white.
"
			);

			if (UseCustomColours.Value)
			{
				SlotColoring.ReadColors(CustomColours.Value);
			}
		}
	}

	internal class TraderPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(TraderClass).GetConstructors()[0];

		[PatchPostfix]
		private static void PatchPostfix(ref TraderClass __instance)
		{
			__instance.UpdateSupplyData();
		}
	}

	internal static class PlayerExtensions
	{
		private static readonly FieldInfo InventoryControllerField =
			typeof(Player).GetField("_inventoryController", BindingFlags.NonPublic | BindingFlags.Instance);

		public static InventoryControllerClass GetInventoryController(this Player player) =>
			InventoryControllerField.GetValue(player) as InventoryControllerClass;
	}

	internal static class Globals
	{
		public static bool isStashItemHovered = false;
		public static ISession Session => ClientAppUtils.GetMainApp().GetClientBackEndSession();
		public static ManualLogSource logger { get; set; }
		public static Item hoveredItem;

		public static TraderOffer GetBestTraderOffer(Item item)
		{
			if (!Session.Profile.Examined(item))
			{
				return null;
			}

			switch (item.Owner?.OwnerType)
			{
				case EOwnerType.Trader:
				case EOwnerType.RagFair:
					if (item.StackObjectsCount > 1 || item.UnlimitedCount)
					{
						item = item.CloneItem();
						item.StackObjectsCount = 1;
						item.UnlimitedCount = false;
					}
					break;
			}

			var trader = Session.GetTrader("broker-trader-id");

			if (GetTraderOffer(item, trader) is TraderOffer offer)
			{
				return offer;
			}

			return null;
		}

		public class FleaPriceRequest
		{
			public string templateId;
			public FleaPriceRequest(string templateId) => this.templateId = templateId;
		}

		private static TraderOffer GetTraderOffer(Item item, TraderClass trader)
		{
			var result = trader.GetUserItemPrice(item);
			if (result == null)
			{
				return null;
			}

			return new TraderOffer(
				trader.Id,
				trader.LocalizedName,
				result.Value.Amount,
				CurrencyUtil.GetCurrencyCharById(result.Value.CurrencyId),
				trader.GetSupplyData().CurrencyCourses[result.Value.CurrencyId],
				item.StackObjectsCount
			);
		}

		public sealed class TraderOffer
		{
			public string TraderId;
			public string TraderName;
			public int Price;
			public string Currency;
			public double Course;
			public int Count;

			public TraderOffer(string traderId, string traderName, int price, string currency, double course, int count)
			{
				TraderId = traderId;
				TraderName = traderName;
				Price = price;
				Currency = currency;
				Course = course;
				Count = count;
			}
		}
	}

	public class ItemShowTooltipPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(GridItemView).GetMethod("ShowTooltip", BindingFlags.Instance | BindingFlags.NonPublic);

		[PatchPrefix]
		static void Prefix(GridItemView __instance)
		{
			if (__instance.Item != null)
			{
				hoveredItem = __instance.Item;
			}
		}
	}

	internal class GridItemOnPointerEnterPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(GridItemView).GetMethod("OnPointerEnter", BindingFlags.Instance | BindingFlags.Public);

		[PatchPrefix]
		static void Prefix(GridItemView __instance, PointerEventData eventData)
		{
			if (__instance.Item != null)
			{
				hoveredItem = __instance.Item;
				Globals.isStashItemHovered = true;
			}
		}
	}

	internal class GridItemOnPointerExitPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(GridItemView).GetMethod("OnPointerExit", BindingFlags.Instance | BindingFlags.Public);

		[PatchPrefix]
		static void Prefix(GridItemView __instance, PointerEventData eventData)
		{
			Globals.isStashItemHovered = false;
			hoveredItem = null;
		}
	}

	public class SellItemToTraderRequest
	{
		public string ItemId;
		public string TraderId;
		public int Price;

		public SellItemToTraderRequest(string itemId, string traderId, int price)
		{
			this.ItemId = itemId;
			this.TraderId = traderId;
			this.Price = price;
		}
	}

	internal class ItemViewOnClickPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(GridItemView).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic);

		private static readonly HashSet<string> ItemSells = new HashSet<string>();

		[PatchPrefix]
		static void Prefix(GridItemView __instance, PointerEventData.InputButton button, Vector2 position, bool doubleClick)
		{
			Item item = __instance.Item;

			if (ItemSells.Contains(item.Id))
			{
				return;
			}

			ItemSells.Add(item.Id);

			if (LootValueMod.EnableQuickSell.Value && !GClass1716.InRaid && item != null)
			{
				if (Input.GetKey(KeyCode.LeftShift) && Input.GetKey(KeyCode.LeftAlt))
				{
					if (button == PointerEventData.InputButton.Left)
					{
						SellToTrader(item);
					}
				}
			}

			ItemSells.Remove(item.Id);
		}

		static void SellToTrader(Item item)
		{
			try
			{
				TraderClass traderClass = Session.GetTrader("broker-trader-id");
				if (traderClass.CurrentAssortment == null)
				{
					traderClass.RefreshAssortment(true, true)
						.ContinueWith(
							task =>
							{
								if (task.IsFaulted)
								{
									return;
								}
								SellWhenAssortmentIsNotNull(item, traderClass);
								ItemSells.Remove(item.Id);
							}, TaskScheduler.FromCurrentSynchronizationContext());
					// NotificationManagerClass.DisplayMessageNotification("You must load the trader first!");
					return;
				}

				SellWhenAssortmentIsNotNull(item, traderClass);
				ItemSells.Remove(item.Id);
			}
			catch (Exception ex)
			{
				ItemSells.Remove(item.Id);

				logger.LogInfo($"Something fucked up: {ex.Message}");
				logger.LogInfo($"{ex.InnerException.Message}");
			}
		}

		private static void SellWhenAssortmentIsNotNull(Item item, TraderClass traderClass)
		{
			TraderAssortmentControllerClass tacc = traderClass.CurrentAssortment;
			tacc.PrepareToSell(item, new LocationInGrid(2, 3, ItemRotation.Horizontal));
			tacc.Sell();
		}
	}

	internal class ShowTooltipPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(SimpleTooltip).GetMethod("Show", BindingFlags.Instance | BindingFlags.Public);

		[PatchPrefix]
		private static void Prefix(ref string text, ref float delay, SimpleTooltip __instance)
		{
			delay = 0;

			if (hoveredItem != null)
			{
				TraderOffer bestTraderOffer = GetBestTraderOffer(hoveredItem);

				int fleaPricePerSlot = 0, traderPricePerSlot = 0;

				var size = hoveredItem.CalculateCellSize();
				int slots = size.X * size.Y;

				if (bestTraderOffer != null)
				{
					double totalTraderPrice = bestTraderOffer.Price;
					traderPricePerSlot = (int)Math.Round(totalTraderPrice / slots);

					SetText(traderPricePerSlot, fleaPricePerSlot, totalTraderPrice, slots, ref text, bestTraderOffer.TraderName);
				}
			}
		}

		private static void SetText(int valuePerSlotA, int valuePerSlotB, double totalValue, int slots, ref string text, string buyer)
		{
			string perSlotColor = SlotColoring.GetColorFromValuePerSlots(valuePerSlotA);
			string highlightText;

			if (valuePerSlotA > valuePerSlotB)
			{
				highlightText = $"<color=#ffffff>{buyer}</color>";
			}
			else
			{
				highlightText = buyer;
			}

			if (LootValueMod.OnlyShowTotalValue.Value)
			{
				text += $"<br>{highlightText}: <color={perSlotColor}>{totalValue.FormatNumber()}</color>";
			}
			else
			{
				text += $"<br>{highlightText}: <color={perSlotColor}>{valuePerSlotA.FormatNumber()}</color>";

				if (slots > 1)
				{
					text += $" Total: {totalValue.FormatNumber()}";
				}
			}
		}
	}
}
