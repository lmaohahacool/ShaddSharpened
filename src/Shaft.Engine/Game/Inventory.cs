using Shaft.Engine.Config;
using Shaft.Engine.Items;

namespace Shaft.Engine.Game;

/// <summary>A single inventory slot. <see cref="Id"/> == 0 means empty.</summary>
public struct ItemStack
{
    public int Id;
    public int Count;
    /// <summary>Remaining durability for tools; 0 when not applicable.</summary>
    public int Durability;

    public bool IsEmpty => Id == 0 || Count <= 0;

    public static ItemStack Empty => default;

    public ItemStack Clone() => this;
}

/// <summary>
/// Port of <c>dist/inventory.js</c>: the 36-slot player inventory (9 hotbar +
/// 27 storage), the held cursor stack, and the stack/transfer rules.
/// </summary>
public sealed class Inventory
{
    public const int SIZE = GameConfig.TOTAL_SLOTS;      // 36
    public const int HOTBAR = GameConfig.HOTBAR_SIZE;    // 9
    public const int MAX_STACK = GameConfig.INVENTORY_MAX_STACK; // 64

    public ItemStack[] Slots { get; } = new ItemStack[SIZE];

    /// <summary>Currently selected hotbar index, 0..8.</summary>
    public int SelectedIndex { get; set; }

    /// <summary>The stack "held" by the mouse while an inventory screen is open.</summary>
    public ItemStack Cursor { get; set; } = ItemStack.Empty;

    /// <summary>Raised whenever contents change so the HUD can redraw.</summary>
    public event Action? Changed;

    public Inventory() { for (int i = 0; i < SIZE; i++) Slots[i] = ItemStack.Empty; }

    private void OnChanged() => Changed?.Invoke();

    public ref ItemStack Slot(int i) => ref Slots[i];

    /// <summary>Port of <c>getSelectedItemId</c>.</summary>
    public int GetSelectedItemId() => Slots[SelectedIndex].Id;

    public ref ItemStack Selected => ref Slots[SelectedIndex];

    /// <summary>Creates a stack, filling in default durability for tools exactly as
    /// <c>makeItem</c> does.</summary>
    public static ItemStack MakeItem(int id, int count)
    {
        int dur = 0;
        if (ItemId.IsItem(id) && ItemTable.TryGet(id, out var info)) dur = info.Durability;
        return new ItemStack { Id = id, Count = count, Durability = dur };
    }

    /// <summary>Port of <c>addToInventory</c>: fill existing stacks first (never tools,
    /// which do not stack), then use empty slots. Leftover is dropped silently,
    /// matching the JS.</summary>
    public void Add(int id, int count, int durability = 0)
    {
        bool isTool = ItemId.IsItem(id) && ItemTable.TryGet(id, out var ti) && ti.Durability > 0;

        if (!isTool)
        {
            for (int i = 0; i < SIZE && count > 0; i++)
            {
                ref var s = ref Slots[i];
                if (s.Id != id || s.Count >= MAX_STACK) continue;
                int space = MAX_STACK - s.Count;
                int toAdd = Math.Min(space, count);
                s.Count += toAdd;
                count -= toAdd;
            }
        }

        for (int i = 0; i < SIZE && count > 0; i++)
        {
            ref var s = ref Slots[i];
            if (!s.IsEmpty) continue;
            int toAdd = Math.Min(MAX_STACK, count);
            Slots[i] = new ItemStack
            {
                Id = id,
                Count = toAdd,
                Durability = durability != 0 ? durability
                    : (ItemId.IsItem(id) && ItemTable.TryGet(id, out var info) ? info.Durability : 0)
            };
            count -= toAdd;
        }

        OnChanged();
    }

    /// <summary>Port of <c>removeFromInventory</c>: consumes from the selected slot
    /// first, then the rest. Returns false without consuming if the total is short.</summary>
    public bool Remove(int id, int count)
    {
        int total = 0;
        for (int i = 0; i < SIZE; i++) if (Slots[i].Id == id) total += Slots[i].Count;
        if (total < count) return false;

        int remaining = count;
        void Take(int i)
        {
            if (remaining <= 0) return;
            ref var s = ref Slots[i];
            if (s.Id != id) return;
            int toRemove = Math.Min(s.Count, remaining);
            s.Count -= toRemove;
            remaining -= toRemove;
            if (s.Count <= 0) Slots[i] = ItemStack.Empty;
        }

        Take(SelectedIndex);
        for (int i = 0; i < SIZE && remaining > 0; i++) if (i != SelectedIndex) Take(i);

        OnChanged();
        return true;
    }

    public int CountOf(int id)
    {
        int total = 0;
        for (int i = 0; i < SIZE; i++) if (Slots[i].Id == id) total += Slots[i].Count;
        return total;
    }

    /// <summary>
    /// Port of <c>clickSlot</c>: the full pick-up / split / deposit / swap state
    /// machine driven by left click, right click and shift.
    /// </summary>
    public void ClickSlot(int index, bool rightClick, bool shiftClick)
        => ClickExternalSlot(ref Slots[index], rightClick, shiftClick);

    /// <summary>
    /// The same state machine applied to a slot the inventory does not own — a chest cell or
    /// a furnace input/fuel/output. <c>inventory.js</c> expressed this once as
    /// <c>clickSlot(getter, setter, rightClick, shiftClick)</c> and every container called it;
    /// this is that generic form, with <see cref="ClickSlot"/> as the array-bound wrapper.
    /// </summary>
    public void ClickExternalSlot(ref ItemStack slot, bool rightClick, bool shiftClick)
    {
        ref var cur = ref slot;
        var cursor = Cursor;

        if (cursor.IsEmpty)
        {
            if (!cur.IsEmpty)
            {
                if (rightClick)
                {
                    if (shiftClick)
                    {
                        cur = ItemStack.Empty;   // shift+right = delete stack
                    }
                    else
                    {
                        int half = cur.Count / 2;
                        if (half > 0)
                        {
                            Cursor = new ItemStack { Id = cur.Id, Count = half, Durability = cur.Durability };
                            cur.Count -= half;
                            if (cur.Count <= 0) cur = ItemStack.Empty;
                        }
                    }
                }
                else
                {
                    Cursor = cur.Clone();
                    cur = ItemStack.Empty;
                }
            }
            OnChanged();
            return;
        }

        if (cur.IsEmpty)
        {
            if (rightClick)
            {
                cur = new ItemStack { Id = cursor.Id, Count = 1, Durability = cursor.Durability };
                cursor.Count--;
                Cursor = cursor.Count <= 0 ? ItemStack.Empty : cursor;
            }
            else
            {
                cur = cursor.Clone();
                Cursor = ItemStack.Empty;
            }
        }
        else if (cur.Id == cursor.Id && cur.Count < MAX_STACK && !rightClick)
        {
            int space = MAX_STACK - cur.Count;
            int move = Math.Min(space, cursor.Count);
            cur.Count += move;
            cursor.Count -= move;
            Cursor = cursor.Count <= 0 ? ItemStack.Empty : cursor;
        }
        else if (rightClick && cur.Id == cursor.Id && cur.Count < MAX_STACK)
        {
            cur.Count++;
            cursor.Count--;
            Cursor = cursor.Count <= 0 ? ItemStack.Empty : cursor;
        }
        else
        {
            var tmp = cur.Clone();
            cur = cursor.Clone();
            Cursor = tmp;
        }

        OnChanged();
    }

    /// <summary>Returns the cursor stack to the inventory, as <c>closeInventory</c> does.</summary>
    public void ReturnCursor()
    {
        if (Cursor.IsEmpty) return;
        Add(Cursor.Id, Cursor.Count, Cursor.Durability);
        Cursor = ItemStack.Empty;
    }

    /// <summary>Port of the survival tool-wear step in <c>breakBlockInstant</c>.
    /// Returns true if the tool broke.</summary>
    public bool DamageSelectedTool(int amount = 1)
    {
        ref var held = ref Slots[SelectedIndex];
        if (held.IsEmpty || !ItemId.IsItem(held.Id)) return false;
        if (!ItemTable.TryGet(held.Id, out var info) || info.Durability <= 0) return false;

        if (held.Durability == 0) held.Durability = info.Durability;
        held.Durability -= amount;
        if (held.Durability <= 0)
        {
            Slots[SelectedIndex] = ItemStack.Empty;
            OnChanged();
            return true;
        }
        OnChanged();
        return false;
    }

    public void Clear()
    {
        for (int i = 0; i < SIZE; i++) Slots[i] = ItemStack.Empty;
        Cursor = ItemStack.Empty;
        OnChanged();
    }
}
