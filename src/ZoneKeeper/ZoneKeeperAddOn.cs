using System;
using System.Collections.Generic;
using System.Windows;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;

namespace NinjaTrader.NinjaScript.AddOns.ZoneKeeper
{
    public sealed class ZoneKeeperAddOn : AddOnBase
    {
        private readonly Dictionary<ControlCenter, NTMenuItem> menus = new Dictionary<ControlCenter, NTMenuItem>();
        private bool started;
        protected override void OnStateChange()
        {
            try
            {
                if (State == State.SetDefaults) { Name = "ZoneKeeper"; Description = "Sends closed trades to getzonekeeper.com"; }
                else if (State == State.Active) started = ZkEngine.Start(this);
                else if (State == State.Terminated && started) { ZkEngine.Stop(this); started = false; }
            }
            catch (Exception ex) { ZkEngine.ReportError("Could not change the ZoneKeeper lifecycle state", ex); }
        }
        protected override void OnWindowCreated(Window window)
        {
            try
            {
                var cc = window as ControlCenter;
                if (cc == null || menus.ContainsKey(cc)) return;
                var parent = cc.FindFirst("ControlCenterMenuItemNew") as NTMenuItem;
                if (parent == null) return;
                foreach (object item in parent.Items)
                { var existing = item as NTMenuItem; if (existing != null && string.Equals(existing.Header as string, "ZoneKeeper", StringComparison.Ordinal)) return; }
                var menu = new NTMenuItem { Header = "ZoneKeeper", Style = Application.Current.TryFindResource("MainMenuItem") as Style };
                menu.Click += OnMenuClick; parent.Items.Add(menu); menus.Add(cc, menu);
            }
            catch (Exception ex) { ZkEngine.ReportError("Could not add the ZoneKeeper menu", ex); }
        }
        protected override void OnWindowDestroyed(Window window)
        {
            try
            {
                var cc = window as ControlCenter;
                NTMenuItem menu;
                if (cc == null || !menus.TryGetValue(cc, out menu)) return;
                menu.Click -= OnMenuClick;
                var parent = cc.FindFirst("ControlCenterMenuItemNew") as NTMenuItem;
                if (parent != null) parent.Items.Remove(menu);
                menus.Remove(cc);
            }
            catch (Exception ex) { ZkEngine.ReportError("Could not remove the ZoneKeeper menu", ex); }
        }
        private void OnMenuClick(object sender, RoutedEventArgs e)
        {
            try { NinjaTrader.Core.Globals.RandomDispatcher.InvokeAsync(new Action(ZoneKeeperWindow.ShowOne)); }
            catch (Exception ex) { ZkEngine.ReportError("Could not open the ZoneKeeper window", ex); }
        }
    }
}
