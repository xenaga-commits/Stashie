namespace OriathHub.Plugins.Stashie
{
    using ClickableTransparentOverlay.Win32;
    using Coroutine;
    using ImGuiNET;
    using OriathHub.Plugin;
    using OriathHub.RemoteEnums;
    using OriathHub.Utils;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Numerics;
    using System.Runtime.InteropServices;

    public sealed class StashieSettings
    {
        public VK TransferHotkey = VK.F6;
        public int MinDelayMs = 100;
        public int MaxDelayMs = 250;
        public int MaxTabsToTry = 5;
        public int InventoryRegisterDelayMs = 50;
    }

    public sealed class Stashie : PluginBase
    {
        private StashieSettings settings = new();
        private FileInfo SettingsFile => new(Path.Combine(DllDirectory, "config", "settings.json"));
        private ActiveCoroutine? transferTask;
        private Random rng = new Random();

        public override string Name => "Stashie";
        public override string Description => "Automates inventory-to-stash item transfers.";

        public override void OnEnable(bool isGameOpened)
        {
            settings = JsonHelper.CreateOrLoadJsonFile<StashieSettings>(SettingsFile);
        }

        public override void OnDisable()
        {
            transferTask?.Cancel();
            transferTask = null;
        }

        public override void DrawSettings()
        {
            if (ImGui.BeginTabBar("StashieTabs"))
            {
                if (ImGui.BeginTabItem("Player Inventory"))
                {
                    ImGui.TextWrapped("Make sure you have your stash tab open. This program will cycle through each inventory slot that has an item and will first use affinity. If there is no affinity or it's full, it will go through the next tab and place items there. It will cycle through each tab until all the player inventory is empty.");
                    ImGui.Spacing();
                    ImGui.Separator();
                    ImGui.Spacing();

                    ImGuiHelper.NonContinuousEnumComboBox("Start/Stop Hotkey", ref settings.TransferHotkey);
                    ImGui.Spacing();

                    ImGui.TextWrapped("Delay for inventory to register (ms)");
                    ImGui.SliderInt("##RegisterDelay", ref settings.InventoryRegisterDelayMs, 10, 500);

                    ImGui.TextWrapped("Min Delay (ms) after each click");
                    ImGui.SliderInt("##MinDelay", ref settings.MinDelayMs, 50, 1000);

                    ImGui.TextWrapped("Max Delay (ms) after each click");
                    ImGui.SliderInt("##MaxDelay", ref settings.MaxDelayMs, 50, 1000);

                    ImGui.TextWrapped("Number of tabs to cycle through in case tab/affinity is full");
                    ImGui.SliderInt("##MaxTabs", ref settings.MaxTabsToTry, 1, 50);

                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("About"))
                {
                    ImGui.TextWrapped("Stashie - Automated Inventory Management for Path of Exile 2");
                    ImGui.Spacing();
                    ImGui.TextWrapped("More info coming soon: Scroll of wisdom and omen exp to be excluded.");
                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }
        }

        public override void DrawUI()
        {
            if (Core.States.GameCurrentState != GameStateTypes.InGameState) return;

            if (Core.Process.Foreground && Utils.IsKeyPressedAndNotTimeout(settings.TransferHotkey))
            {
                if (transferTask == null || transferTask.IsFinished)
                {
                    Log.Info(">>> STARTED Stashie Transfer <<<", Name);
                    transferTask = CoroutineHandler.Start(TransferLoop(), "Stashie.Transfer");
                }
                else
                {
                    Log.Info("<<< STOPPED Stashie Transfer <<<", Name);
                    transferTask.Cancel();
                    transferTask = null;
                }
            }
        }

        private IEnumerator<Wait> TransferLoop()
        {
            var winArea = Core.Process.WindowArea;
            HashSet<Vector2> ignoredItems = new HashSet<Vector2>(); 

            while (true)
            {
                var rp = Core.States.InGameStateObject.GameUi.RightPanel;
                if (rp == null || !rp.IsVisible) yield break;

                var grid = rp[5]?[36];
                if (grid == null || !grid.IsVisible)
                {
                    for (int w = 0; w < rp.TotalChildrens; w++)
                    {
                        var wrapped = rp[w]?[5]?[36];
                        if (wrapped != null && wrapped.IsVisible) { grid = wrapped; break; }
                    }
                }

                if (grid == null || grid.TotalChildrens <= 1) 
                {
                    Log.Info("Transfer complete or no items found.", Name);
                    break;
                }

                bool foundItem = false;
                Vector2 targetCenter = Vector2.Zero;

                for (int i = 1; i < grid.TotalChildrens; i++)
                {
                    var slot = grid[i];
                    if (slot != null && slot.Size.X > 10 && slot.Size.Y > 10)
                    {
                        var center = slot.Position + (slot.Size / 2f);
                        if (!ignoredItems.Contains(center))
                        {
                            foundItem = true;
                            targetCenter = center;
                            break;
                        }
                    }
                }

                if (!foundItem)
                {
                     Log.Info("All items stashed or blacklisted.", Name);
                     break; 
                }

                int clickX = winArea.X + (int)targetCenter.X;
                int clickY = winArea.Y + (int)targetCenter.Y;

                bool itemMoved = false;
                int tabsTried = 0;

                bool HasItemMoved()
                {
                    var checkRp = Core.States.InGameStateObject.GameUi.RightPanel;
                    if (checkRp == null) return true; 

                    var checkGrid = checkRp[5]?[36];
                    if (checkGrid == null || !checkGrid.IsVisible) 
                    {
                        for (int w = 0; w < checkRp.TotalChildrens; w++) 
                        {
                            var wrapped = checkRp[w]?[5]?[36];
                            if (wrapped != null && wrapped.IsVisible) { checkGrid = wrapped; break; }
                        }
                    }
                    if (checkGrid == null || checkGrid.TotalChildrens < grid.TotalChildrens) return true;
                    return false;
                }

                while (!itemMoved && tabsTried < settings.MaxTabsToTry)
                {
                    SetCursorPos(clickX, clickY);
                    yield return new Wait(0.05);

                    // ACTION 1: Ctrl + Right Click
                    keybd_event(VK_CONTROL, 0, 0, 0);
                    yield return new Wait(settings.InventoryRegisterDelayMs / 1000f); 
                    mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, 0);
                    yield return new Wait(0.02);
                    mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, 0);
                    yield return new Wait(0.02);
                    keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, 0);
                    yield return new Wait(rng.Next(settings.MinDelayMs, settings.MaxDelayMs + 1) / 1000f);
                    if (HasItemMoved()) { itemMoved = true; break; }

                    // ACTION 2: Ctrl + Shift + Right Click
                    keybd_event(VK_CONTROL, 0, 0, 0);
                    keybd_event(VK_SHIFT, 0, 0, 0);
                    yield return new Wait(settings.InventoryRegisterDelayMs / 1000f); 
                    mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, 0);
                    yield return new Wait(0.02);
                    mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, 0);
                    yield return new Wait(0.02);
                    keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, 0);
                    keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, 0);
                    yield return new Wait(rng.Next(settings.MinDelayMs, settings.MaxDelayMs + 1) / 1000f);
                    if (HasItemMoved()) { itemMoved = true; break; }

                    // ACTION 3: Right Arrow
                    // CORRECTED: Reference VK.RIGHT enum value
                    MiscHelper.KeyUp(VK.RIGHT);
                    yield return new Wait(0.15);

                    // ACTION 4: Ctrl + Shift + Right Click
                    keybd_event(VK_CONTROL, 0, 0, 0);
                    keybd_event(VK_SHIFT, 0, 0, 0);
                    yield return new Wait(settings.InventoryRegisterDelayMs / 1000f); 
                    mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, 0);
                    yield return new Wait(0.02);
                    mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, 0);
                    yield return new Wait(0.02);
                    keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, 0);
                    keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, 0);
                    yield return new Wait(rng.Next(settings.MinDelayMs, settings.MaxDelayMs + 1) / 1000f);
                    if (HasItemMoved()) { itemMoved = true; break; }

                    tabsTried++;
                }

                if (!itemMoved)
                {
                    Log.Warning("Item stuck. Add to blacklist.", Name);
                    ignoredItems.Add(targetCenter);
                }
            }
        }

        public override void SaveSettings()
        {
            JsonHelper.SaveToFile(settings, SettingsFile);
        }

        [DllImport("user32.dll")] static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint cButtons, uint dwExtraInfo);
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, uint dwExtraInfo);
        
        const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        const byte VK_CONTROL = 0x11;
        const byte VK_SHIFT = 0x10;
        const uint KEYEVENTF_KEYUP = 0x0002;
    }
}