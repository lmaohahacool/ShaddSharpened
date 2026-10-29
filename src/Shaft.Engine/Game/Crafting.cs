using Shaft.Engine.Items;

namespace Shaft.Engine.Game;

/// <summary>
/// Port of <c>matchRecipe</c> and the crafting-grid lifecycle from
/// <c>dist/crafting.js</c>.
/// </summary>
public sealed class CraftingSystem
{
    /// <summary>The grid currently laid out: 4 slots for the 2x2 inventory grid,
    /// 9 for the 3x3 crafting table.</summary>
    public ItemStack[] Grid { get; private set; } = new ItemStack[4];

    public int Size { get; private set; } = 2;

    /// <summary>True while a crafting table (rather than the plain inventory) is open,
    /// which is what selects the 3x3 grid.</summary>
    public bool TableOpen { get; private set; }

    public bool Open { get; private set; }

    /// <summary>Port of <c>openInventory</c>: resets to a fresh 2x2 grid.</summary>
    public void OpenInventory()
    {
        Open = true;
        TableOpen = false;
        Size = 2;
        Grid = new ItemStack[4];
    }

    /// <summary>Port of <c>openCraftingTable</c>: fresh 3x3 grid.</summary>
    public void OpenCraftingTable()
    {
        Open = true;
        TableOpen = true;
        Size = 3;
        Grid = new ItemStack[9];
    }

    /// <summary>Port of <c>closeInventory</c>'s grid-return step. The caller pushes
    /// the returned stacks into the player inventory.</summary>
    public IEnumerable<ItemStack> CloseAndCollect()
    {
        foreach (var s in Grid)
            if (!s.IsEmpty) yield return s;
        Open = false;
        TableOpen = false;
    }

    public void ClickGridSlot(int index, bool rightClick, bool shiftClick, Inventory inv)
    {
        // Same state machine as the inventory, operating on the grid array.
        var saved = inv.Cursor;
        var cur = Grid[index];

        if (saved.IsEmpty)
        {
            if (!cur.IsEmpty)
            {
                if (rightClick)
                {
                    if (shiftClick) Grid[index] = ItemStack.Empty;
                    else
                    {
                        int half = cur.Count / 2;
                        if (half > 0)
                        {
                            inv.Cursor = new ItemStack { Id = cur.Id, Count = half, Durability = cur.Durability };
                            cur.Count -= half;
                            Grid[index] = cur.Count <= 0 ? ItemStack.Empty : cur;
                        }
                    }
                }
                else
                {
                    inv.Cursor = cur.Clone();
                    Grid[index] = ItemStack.Empty;
                }
            }
            return;
        }

        if (cur.IsEmpty)
        {
            if (rightClick)
            {
                Grid[index] = new ItemStack { Id = saved.Id, Count = 1, Durability = saved.Durability };
                saved.Count--;
                inv.Cursor = saved.Count <= 0 ? ItemStack.Empty : saved;
            }
            else
            {
                Grid[index] = saved.Clone();
                inv.Cursor = ItemStack.Empty;
            }
        }
        else if (cur.Id == saved.Id && !rightClick)
        {
            cur.Count += saved.Count;
            Grid[index] = cur;
            inv.Cursor = ItemStack.Empty;
        }
        else if (rightClick && cur.Id == saved.Id)
        {
            cur.Count++;
            Grid[index] = cur;
            saved.Count--;
            inv.Cursor = saved.Count <= 0 ? ItemStack.Empty : saved;
        }
        else
        {
            Grid[index] = saved.Clone();
            inv.Cursor = cur.Clone();
        }
    }

    /// <summary>
    /// Port of <c>matchRecipe</c>. Scans every recipe in table order and every
    /// placement offset within the grid; a recipe matches only if the pattern fits
    /// exactly AND every cell outside the pattern's bounding box is empty. First
    /// match wins — which is why a 2x2 of planks yields a crafting table rather
    /// than a bed, exactly as in the JS build.
    /// </summary>
    public static CraftRecipe? MatchRecipe(IReadOnlyList<int> grid, int size)
    {
        foreach (var r in CraftTable.Recipes)
        {
            int ph = r.Height, pw = r.Width;
            if (ph > size || pw > size) continue;

            for (int oy = 0; oy <= size - ph; oy++)
            for (int ox = 0; ox <= size - pw; ox++)
            {
                bool ok = true;
                for (int y = 0; y < ph && ok; y++)
                for (int x = 0; x < pw; x++)
                {
                    int want = r.Pattern[y, x];
                    int have = grid[(oy + y) * size + (ox + x)];
                    if (want == 0) { if (have != 0) { ok = false; break; } }
                    else if (want != have) { ok = false; break; }
                }
                if (!ok) continue;

                bool clean = true;
                for (int y = 0; y < size && clean; y++)
                for (int x = 0; x < size; x++)
                {
                    bool inside = y >= oy && y < oy + ph && x >= ox && x < ox + pw;
                    if (inside) continue;
                    if (grid[y * size + x] != 0) { clean = false; break; }
                }
                if (clean) return r;
            }
        }
        return null;
    }

    /// <summary>Current grid as a flat id list, for <see cref="MatchRecipe"/>.</summary>
    public int[] GridIds()
    {
        var g = new int[Grid.Length];
        for (int i = 0; i < Grid.Length; i++) g[i] = Grid[i].IsEmpty ? 0 : Grid[i].Id;
        return g;
    }

    /// <summary>The recipe the current grid produces, or null.</summary>
    public CraftRecipe? CurrentRecipe() => MatchRecipe(GridIds(), Size);

    /// <summary>
    /// Port of the output-slot click handler: hands over the result and decrements
    /// every occupied grid cell by one. <paramref name="single"/> corresponds to a
    /// right click, which crafts one item instead of the full result count.
    /// </summary>
    public ItemStack? TakeOutput(bool single)
    {
        var rec = CurrentRecipe();
        if (rec is null) return null;

        var result = Inventory.MakeItem(rec.ResultId, single ? 1 : rec.ResultCount);
        for (int i = 0; i < Grid.Length; i++)
        {
            var it = Grid[i];
            if (it.IsEmpty) continue;
            it.Count--;
            Grid[i] = it.Count <= 0 ? ItemStack.Empty : it;
        }
        return result;
    }
}
