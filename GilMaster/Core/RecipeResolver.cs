using GilMaster.Models;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GilMaster.Core;

public sealed class RecipeResolver
{
    // Gatherables built by GatheringLocator — used to classify ingredients
    private HashSet<uint> gatherableItemIds = [];

    // Simple shop-buyable items: crystals/shards and common vendor items.
    // In FFXIV, crystals are sold by material suppliers. Row IDs 2-19 are crystals/shards/clusters.
    private static bool IsLikelyShopBuyable(uint itemId) => itemId is >= 2 and <= 19;

    public void SetGatherables(HashSet<uint> ids) => gatherableItemIds = ids;

    // Returns a deep recipe tree for the given recipe.
    public RecipeIngredient[] Resolve(uint recipeId, int multiplier = 1)
    {
        var recipeSheet = Service.DataManager.GetExcelSheet<Recipe>();
        var recipe = recipeSheet.GetRowOrDefault(recipeId);
        if (recipe is null) return [];

        var ingredients = new List<RecipeIngredient>();
        var ingredientList = recipe.Value.Ingredient.ToArray();
        var amounts = recipe.Value.AmountIngredient.ToArray();

        for (var i = 0; i < ingredientList.Length && i < amounts.Length; i++)
        {
            var ingRef = ingredientList[i];
            if (!ingRef.IsValid || ingRef.RowId == 0) continue;
            var amount = amounts[i];
            if (amount == 0) continue;

            var ingItem = ingRef.ValueNullable;
            if (ingItem is null) continue;

            var itemId = ingRef.RowId;
            var isGatherable = gatherableItemIds.Contains(itemId);
            var subRecipeId = FindRecipeFor(itemId);
            var isCraftable = subRecipeId.HasValue;
            var isShopBuyable = IsLikelyShopBuyable(itemId);

            ingredients.Add(new RecipeIngredient
            {
                ItemId = itemId,
                Name = ingItem.Value.Name.ToString(),
                Quantity = amount * multiplier,
                CanBeHq = ingItem.Value.CanBeHq,
                IsGatherable = isGatherable,
                IsCraftable = isCraftable,
                IsShopBuyable = isShopBuyable,
                ShopPrice = isShopBuyable ? EstimateShopPrice(itemId) : 0,
                // Sub-ingredients shown one level deep (not recursive to avoid complexity)
                SubIngredients = isCraftable
                    ? Resolve(subRecipeId!.Value, amount * multiplier)
                    : [],
            });
        }

        return [.. ingredients];
    }

    // Flat list — all leaf ingredients (what you actually need to gather/buy), deduplicated.
    public RecipeIngredient[] ResolveFlat(uint recipeId)
    {
        var tree = Resolve(recipeId);
        var flat = new Dictionary<uint, RecipeIngredient>();
        Flatten(tree, flat);
        return [.. flat.Values];
    }

    private void Flatten(RecipeIngredient[] ingredients, Dictionary<uint, RecipeIngredient> into)
    {
        foreach (var ing in ingredients)
        {
            if (ing.IsCraftable && ing.SubIngredients.Length > 0)
            {
                Flatten(ing.SubIngredients, into);
            }
            else
            {
                if (into.TryGetValue(ing.ItemId, out var existing))
                {
                    // Accumulate quantities for duplicate entries across sub-recipes
                    var updated = new RecipeIngredient
                    {
                        ItemId = existing.ItemId,
                        Name = existing.Name,
                        Quantity = existing.Quantity + ing.Quantity,
                        CanBeHq = existing.CanBeHq,
                        IsGatherable = existing.IsGatherable,
                        IsCraftable = existing.IsCraftable,
                        IsShopBuyable = existing.IsShopBuyable,
                        ShopPrice = existing.ShopPrice,
                        MarketMinPrice = existing.MarketMinPrice,
                        MarketPriceFetched = existing.MarketPriceFetched,
                    };
                    into[ing.ItemId] = updated;
                }
                else
                {
                    into[ing.ItemId] = ing;
                }
            }
        }
    }

    // itemResultId → first recipe that produces it. Built once; a linear sheet
    // scan per ingredient was far too slow once we price every result's mats.
    private static Dictionary<uint, uint>? _itemToRecipe;

    // Looks up the first recipe that produces this item.
    public uint? FindRecipeFor(uint itemId)
    {
        _itemToRecipe ??= BuildItemToRecipe();
        return _itemToRecipe.TryGetValue(itemId, out var recipeId) ? recipeId : null;
    }

    private static Dictionary<uint, uint> BuildItemToRecipe()
    {
        var map = new Dictionary<uint, uint>();
        foreach (var recipe in Service.DataManager.GetExcelSheet<Recipe>())
        {
            var id = recipe.ItemResult.RowId;
            if (id != 0 && !map.ContainsKey(id)) map[id] = recipe.RowId;
        }
        return map;
    }

    private static long EstimateShopPrice(uint itemId)
    {
        // Shards = 3g, Crystals = 4g, Clusters = 6g (approximate vendor prices)
        return itemId switch
        {
            >= 2 and <= 7 => 3,     // shards
            >= 8 and <= 13 => 4,    // crystals
            >= 14 and <= 19 => 6,   // clusters
            _ => 0,
        };
    }
}
