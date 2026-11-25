using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Equipment;
using GamesByMalcs.Melvor2.Core;
using GamesByMalcs.Melvor2.Components.Inventories;
using GamesByMalcs.Melvor2.Components.ItemEquipping;
using GamesByMalcs.Melvor2.Components.Loadouts;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Bank;
using GamesByMalcs.Melvor2.Entities.Combat;
using Godot;
using MelvorModLoader;

namespace LoadoutPicker;

// Adds a loadout dropdown to the bank's Equip Item panel.
// Equipping with a non-active loadout picked swaps to it, runs the game's own equip, then swaps back.
public class LoadoutPickerMod : IMod
{
    const int nodeCheckInterval = 30;
    const string pickerName = "LoadoutPickerDropdown";
    const BindingFlags privateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    static readonly PropertyInfo equipOptionsProp = typeof(SelectedItemView).GetProperty("EquipOptions", privateInstance);
    static readonly PropertyInfo viewStatsButtonProp = typeof(EquipItemOptions).GetProperty("ViewStatsButton", privateInstance);
    static readonly PropertyInfo equipButtonContainerProp = typeof(EquipItemOptions).GetProperty("EquipButtonContainer", privateInstance);
    static readonly PropertyInfo itemEquippingProp = typeof(EquipItemOptions).GetProperty("ItemEquipping", privateInstance);
    static readonly PropertyInfo stackProp = typeof(EquipItemOptions).GetProperty("Stack", privateInstance);
    static readonly FieldInfo selectedEventField = typeof(EquipItemButton).GetField("Selected", privateInstance);
    static readonly PropertyInfo availableSlotProp = typeof(EquipItemButton).GetProperty("AvailableSlot", privateInstance);
    static readonly PropertyInfo equipmentLoadoutsProp = typeof(EquipmentComponent).GetProperty("Loadouts", privateInstance);

    ModContext ctx;
    Game cachedGame;
    LoadoutComponent loadouts;
    EquipItemOptions equipOptions;
    Control picker;
    List<ActiveLoadoutSlot> pickerEntries = new List<ActiveLoadoutSlot>();
    string pickerSignature;

    // null means the active loadout.
    LoadoutSlot target;
    LoadoutSlot swappedFrom;

    EventHandler<SlotSelectedEventArgs> beforeEquip, afterEquip;

    public void OnLoad(ModContext ctx)
    {
        this.ctx = ctx;
        beforeEquip = BeforeEquip;
        afterEquip = AfterEquip;
        if (new object[] { equipOptionsProp, viewStatsButtonProp, equipButtonContainerProp, itemEquippingProp, stackProp,
                selectedEventField, availableSlotProp, equipmentLoadoutsProp }.Any(m => m == null))
        {
            ctx.Log("game internals changed, LoadoutPicker disabled");
            return;
        }
        ctx.Frame += Update;
    }

    void Update()
    {
        // Fallback: a swap that was not undone (game equip threw) gets undone here.
        if (swappedFrom != null) SwapBack();

        var game = GameAccess.CurrentGame;
        if (game == null || !game.IsSaveLoaded)
        {
            cachedGame = null;
            loadouts = null;
            return;
        }
        if (game != cachedGame)
        {
            cachedGame = game;
            loadouts = game.Entities.GetAllObjects().OfType<CombatEntity>().FirstOrDefault()?.Loadouts;
            target = null;
        }
        if (loadouts == null) return;

        if (!GodotObject.IsInstanceValid(equipOptions))
        {
            equipOptions = null;
            if (Engine.GetProcessFrames() % nodeCheckInterval != 0) return;
            var selectedItemView = GameAccess.FindNode<SelectedItemView>();
            if (selectedItemView == null) return;
            equipOptions = equipOptionsProp.GetValue(selectedItemView) as EquipItemOptions;
            if (equipOptions == null) return;
        }

        if (!GodotObject.IsInstanceValid(picker)) BuildPicker();
        if (picker == null || !equipOptions.Visible) return;

        RefreshPicker();
        if (target != null && target == loadouts.CurrentLoadout.Data) target = null;
        HookEquipButtons();
    }

    // Same resources as the combat view's loadout picker (combat_active_view.tscn, loadout_button.tscn).
    const string barStylePath = "res://Assets/UI/StyleBoxes/dropdown_panel_normal.tres";
    const string selectedStylePath = "res://Assets/Styles/Button/panel_selected.tres";
    const string listStylePath = "res://Assets/UI/StyleBoxes/btn_textured_normal_dark_8x.tres";
    const string rowStyleDir = "res://Assets/UI/StyleBoxes/";
    const string arrowPath = "res://Assets/UI/Textures/Dropdown/TEX_Combat_19.png";
    static readonly Color gold = new Color("ffc100");
    const float hoverBrightness = 1.2f;
    const int arrowSize = 20;
    const int rowHeight = 34;
    const int rowSpacing = 6;
    const int selectedMargin = 3;

    Button header;
    PopupPanel listPopup;
    VBoxContainer listRows;

    static StyleBox LoadStyle(string path, float left, float top, float right, float bottom, float brightness = 1f)
    {
        if (!ResourceLoader.Exists(path)) return null;
        var style = GD.Load<StyleBox>(path)?.Duplicate() as StyleBox;
        if (style == null) return null;
        style.ContentMarginLeft = left;
        style.ContentMarginTop = top;
        style.ContentMarginRight = right;
        style.ContentMarginBottom = bottom;
        if (style is StyleBoxTexture textured && brightness != 1f)
            textured.ModulateColor = new Color(brightness, brightness, brightness);
        return style;
    }

    static StyleBox LoadStyle(string path) => ResourceLoader.Exists(path) ? GD.Load<StyleBox>(path) : null;

    static Texture2D ScaledTexture(string path, int size)
    {
        if (!ResourceLoader.Exists(path)) return null;
        var image = GD.Load<Texture2D>(path)?.GetImage();
        if (image == null) return null;
        image.Decompress();
        image.Resize(size, size, Image.Interpolation.Lanczos);
        return ImageTexture.CreateFromImage(image);
    }

    static void SetStyles(Button button, StyleBox normal, StyleBox hover, StyleBox pressed, StyleBox hoverPressed, StyleBox disabled)
    {
        if (normal != null) button.AddThemeStyleboxOverride("normal", normal);
        if (hover != null) button.AddThemeStyleboxOverride("hover", hover);
        if (pressed != null) button.AddThemeStyleboxOverride("pressed", pressed);
        if (hoverPressed != null) button.AddThemeStyleboxOverride("hover_pressed", hoverPressed);
        if (disabled != null) button.AddThemeStyleboxOverride("disabled", disabled);
        button.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        button.FocusMode = Control.FocusModeEnum.None;
    }

    void BuildPicker()
    {
        picker = null;
        var viewStats = viewStatsButtonProp.GetValue(equipOptions) as Control;
        if (viewStats == null) return;
        var row = viewStats.GetParent();
        var existing = row.GetNodeOrNull<Control>(pickerName);
        if (existing != null) existing.QueueFree();

        header = new Button();
        header.Name = pickerName;
        header.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        header.SizeFlagsVertical = viewStats.SizeFlagsVertical;
        header.ClipText = true;
        header.Alignment = HorizontalAlignment.Center;
        header.IconAlignment = HorizontalAlignment.Right;
        header.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
        var bar = LoadStyle(barStylePath, 10, 2, 4, 2);
        var barHover = LoadStyle(barStylePath, 10, 2, 4, 2, hoverBrightness);
        SetStyles(header, bar, barHover, barHover, barHover, bar);
        foreach (var state in new[] { "font_hover_color", "font_pressed_color", "font_hover_pressed_color", "font_focus_color" })
            header.AddThemeColorOverride(state, Colors.White);
        int arrow = Math.Max(arrowSize, (int)viewStats.Size.Y - 4);
        var arrowTexture = ScaledTexture(arrowPath, arrow);
        if (arrowTexture != null) header.Icon = arrowTexture;
        header.Pressed += ToggleList;

        listPopup = new PopupPanel();
        listPopup.PopupHide += () => listHiddenAt = Time.GetTicksMsec();
        var listStyle = LoadStyle(listStylePath, 6, 6, 6, 6);
        if (listStyle != null) listPopup.AddThemeStyleboxOverride("panel", listStyle);
        listRows = new VBoxContainer();
        listRows.AddThemeConstantOverride("separation", rowSpacing);
        listPopup.AddChild(listRows);
        header.AddChild(listPopup);

        row.AddChild(header);
        row.MoveChild(header, viewStats.GetIndex());
        picker = header;
        pickerSignature = null;
    }

    // A click on the header while the list is open closes the list on mouse down,
    // then fires Pressed on mouse up. Ignoring Pressed right after a close stops the reopen.
    const ulong reopenGuardMs = 250;
    ulong listHiddenAt;

    void ToggleList()
    {
        if (listPopup.Visible)
        {
            listPopup.Hide();
            return;
        }
        if (Time.GetTicksMsec() - listHiddenAt < reopenGuardMs) return;
        float scale = header.GetGlobalTransform().Scale.Y;
        var position = header.GetScreenPosition() + new Vector2(0, header.Size.Y * scale);
        listPopup.Popup(new Rect2I((Vector2I)position, new Vector2I((int)(header.Size.X * scale), 0)));
    }

    void RefreshPicker()
    {
        var current = loadouts.CurrentLoadout;
        var unlocked = loadouts.LoadoutDisplayOrder.Where(l => l.IsUnlocked).ToList();

        var sig = new StringBuilder();
        foreach (var l in unlocked) sig.Append(l.Data.Id).Append('|').Append(l.Name).Append('|');
        sig.Append(current.Data.Id).Append('|').Append(target?.Id);
        string signature = sig.ToString();
        if (signature == pickerSignature) return;
        pickerSignature = signature;
        pickerEntries = unlocked;

        var picked = target == null ? current : unlocked.FirstOrDefault(l => l.Data == target) ?? current;
        header.Text = picked.Name;

        foreach (var child in listRows.GetChildren()) child.QueueFree();
        var selectedStyle = LoadStyle(selectedStylePath, selectedMargin, selectedMargin, selectedMargin, selectedMargin);
        var unselectedStyle = new StyleBoxEmpty();
        unselectedStyle.SetContentMarginAll(selectedMargin);
        var normal = LoadStyle(rowStyleDir + "btn_textured_normal_8x.tres");
        var hover = LoadStyle(rowStyleDir + "btn_textured_hover_8x.tres");
        var pressed = LoadStyle(rowStyleDir + "btn_textured_pressed_8x.tres");
        var hoverPressed = LoadStyle(rowStyleDir + "btn_textured_hover_pressed_8x.tres");
        var disabled = LoadStyle(rowStyleDir + "btn_textured_disabled_8x.tres");

        for (int i = 0; i < unlocked.Count; i++)
        {
            var l = unlocked[i];
            var frame = new PanelContainer();
            frame.AddThemeStyleboxOverride("panel", l == picked && selectedStyle != null ? selectedStyle : unselectedStyle);

            var rowButton = new Button();
            rowButton.Text = l == current ? l.Name + " (active)" : l.Name;
            rowButton.CustomMinimumSize = new Vector2(0, rowHeight);
            rowButton.ClipText = true;
            rowButton.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
            SetStyles(rowButton, normal, hover, pressed, hoverPressed, disabled);
            rowButton.AddThemeColorOverride("font_hover_color", gold);
            rowButton.AddThemeColorOverride("font_hover_pressed_color", gold);
            int index = i;
            rowButton.Pressed += () => PickerItemSelected(index);

            frame.AddChild(rowButton);
            listRows.AddChild(frame);
        }
        header.Visible = unlocked.Count > 1;
        UpdateReplaceLabels();
    }

    void PickerItemSelected(int index)
    {
        listPopup.Hide();
        if (index < 0 || index >= pickerEntries.Count) return;
        var slot = pickerEntries[index].Data;
        target = slot == loadouts.CurrentLoadout.Data ? null : slot;
        pickerSignature = null;
    }

    // Keeps BeforeEquip first and AfterEquip last around the game's own Selected handler.
    // Rebuilt every frame because the button pool removes and re-adds the game's handler.
    void HookEquipButtons()
    {
        var container = equipButtonContainerProp.GetValue(equipOptions) as Node;
        if (container == null) return;
        foreach (var child in container.GetChildren())
        {
            if (child is not EquipItemButton button) continue;
            var handlers = selectedEventField.GetValue(button) as Delegate;
            var list = handlers?.GetInvocationList() ?? Array.Empty<Delegate>();
            bool ordered = list.Length >= 2 && list[0].Equals(beforeEquip) && list[^1].Equals(afterEquip)
                && list.Skip(1).Take(list.Length - 2).All(d => !d.Equals(beforeEquip) && !d.Equals(afterEquip));
            if (!ordered)
            {
                var game = list.Where(d => !d.Equals(beforeEquip) && !d.Equals(afterEquip));
                var rebuilt = Delegate.Combine(new Delegate[] { beforeEquip }.Concat(game).Append(afterEquip).ToArray());
                selectedEventField.SetValue(button, rebuilt);
            }
        }
        UpdateReplaceLabels();
    }

    void BeforeEquip(object sender, SlotSelectedEventArgs e)
    {
        if (target == null || loadouts == null) return;
        var current = loadouts.CurrentLoadout.Data;
        if (target == current) return;
        if (!loadouts.ActiveLoadouts.TryGetValue(target, out var slot) || !slot.IsUnlocked) return;
        swappedFrom = current;
        loadouts.SwapToLoadout(target);
    }

    void AfterEquip(object sender, SlotSelectedEventArgs e)
    {
        if (swappedFrom == null) return;
        SwapBack();
        pickerSignature = null;
    }

    void SwapBack()
    {
        var back = swappedFrom;
        swappedFrom = null;
        if (loadouts != null && back != null) loadouts.SwapToLoadout(back);
    }

    // A slot of a stored (non-active) loadout, shaped so the game's ReplacedItemView can draw it.
    sealed class StoredSlot : IActiveEquipmentSlot
    {
        public StoredSlot(EquipmentSlot slot, Vector2I gridPosition, EquipmentItem equipped, int quantity)
        {
            Slot = slot;
            GridPosition = gridPosition;
            Equipped = equipped;
            Quantity = quantity;
        }

        public EquipmentSlot Slot { get; }
        public Vector2I GridPosition { get; }
        public EquipmentItem Equipped { get; }
        public int Quantity { get; }
        public bool IsOccupied => false;
        public bool IsEmpty => Equipped == null;
        public event EventHandler<ValueChangedEventArgs<EquipmentItem>> EquippedChanged { add { } remove { } }
        public event EventHandler<ValueChangedEventArgs<int>> QuantityChanged { add { } remove { } }
        public event EventHandler<ValueChangedEventArgs<bool>> IsOccupiedChanged { add { } remove { } }
    }

    // Bindings this mod gave to equip buttons, with the loadout they describe.
    readonly Dictionary<AvailableEquipmentSlot, LoadoutSlot> ownBindings = new Dictionary<AvailableEquipmentSlot, LoadoutSlot>();

    // With a non-active loadout picked, rebinds each equip button so the game's own
    // "Will Replace" rows list that loadout's items. The game rebinds the buttons with
    // active-loadout data on every equip or swap; the next frame rebinds them again.
    void UpdateReplaceLabels()
    {
        var container = equipButtonContainerProp.GetValue(equipOptions) as Node;
        if (container == null) return;
        var stack = stackProp.GetValue(equipOptions) as IReadonlyItemStack;
        var equipping = itemEquippingProp.GetValue(equipOptions) as ItemEquippingComponent;

        if (target == null || stack == null || equipping == null)
        {
            if (ownBindings.Count == 0) return;
            bool anyOwn = container.GetChildren().OfType<EquipItemButton>()
                .Any(b => availableSlotProp.GetValue(b) is AvailableEquipmentSlot a && ownBindings.ContainsKey(a));
            ownBindings.Clear();
            if (anyOwn) equipOptions.Update();
            return;
        }

        foreach (var child in container.GetChildren())
        {
            if (child is not EquipItemButton button) continue;
            if (availableSlotProp.GetValue(button) is not AvailableEquipmentSlot available) continue;
            if (ownBindings.TryGetValue(available, out var describes) && describes == target) continue;

            var replaces = StoredReplaces(equipping.Equipment, stack.Item, available.Slot.Slot);
            var rebound = new AvailableEquipmentSlot(available.Slot, replaces);
            ownBindings.Remove(available);
            ownBindings[rebound] = target;
            button.Unbind();
            button.Bind(rebound);
        }
        if (ownBindings.Count > 32) ownBindings.Clear();
    }

    // Same rules as EquipmentComponent.GetReplacedSlotsOnEquip, read from the stored loadout.
    List<IActiveEquipmentSlot> StoredReplaces(EquipmentComponent equipment, Item item, EquipmentSlot slot)
    {
        var result = new List<IActiveEquipmentSlot>();
        if (equipmentLoadoutsProp.GetValue(equipment) is not IDictionary stored
            || !stored.Contains(target) || stored[target] is not EquipmentLoadout saved)
            return result;

        var added = new HashSet<EquipmentSlot>();
        void AddHolderOf(EquipmentSlot s)
        {
            if (!saved.Slots.TryGetValue(s, out var entry) || entry.Equipped == null) return;
            var holder = s;
            var holderEntry = entry;
            if (entry.IsOccupied)
            {
                var owner = saved.Slots.FirstOrDefault(kv => !kv.Value.IsOccupied && kv.Value.Equipped?.Item == entry.Equipped.Item);
                if (owner.Value == null) return;
                holder = owner.Key;
                holderEntry = owner.Value;
            }
            if (!added.Add(holder)) return;
            var grid = equipment.TryGetActiveSlot(holder, out var active) ? active.GridPosition : Vector2I.Zero;
            result.Add(new StoredSlot(holder, grid, holderEntry.Equipped, holderEntry.Quantity));
        }

        AddHolderOf(slot);
        if (item.Capabilities.TryGet<EquipmentCapability>(out var cap))
        {
            if (cap.OccupiesSlots != null)
                foreach (var s in cap.OccupiesSlots) AddHolderOf(s);
            if (cap.AmmoType != null)
                foreach (var kv in saved.Slots)
                    if (!kv.Value.IsOccupied && kv.Value.Equipped?.Equipment.AmmoType == cap.AmmoType) AddHolderOf(kv.Key);
        }
        if (item.Capabilities.Contains<WeaponCapability>())
            foreach (var kv in saved.Slots)
                if (!kv.Value.IsOccupied && kv.Value.Equipped != null && kv.Value.Equipped.Item.Capabilities.Contains<WeaponCapability>())
                    AddHolderOf(kv.Key);
        return result;
    }
}
