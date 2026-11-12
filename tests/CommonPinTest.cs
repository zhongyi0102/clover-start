using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Clover
{
    // 保留套件名，验证取消固定后的统一常用，不再测试旧固定功能。
    static class CommonPinTest
    {
        static int passed, failed;
        static void Check(bool ok, string name)
        {
            if (ok) passed++; else failed++;
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name);
        }
        static void Wait(PanelWindow panel)
        {
            for (int i = 0; i < 100 && !panel.TestDeferredRenderComplete(); i++)
            {
                Dispatcher.CurrentDispatcher.Invoke(new Action(delegate { }), DispatcherPriority.ApplicationIdle);
                System.Threading.Thread.Sleep(20);
            }
            panel.UpdateLayout();
        }
        static bool Text(DependencyObject node, string value)
        {
            TextBlock t = node as TextBlock;
            if (t != null && t.Text.Contains(value)) return true;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                if (Text(VisualTreeHelper.GetChild(node, i), value)) return true;
            return false;
        }
        static Button ResetButton(DependencyObject node)
        {
            Button b = node as Button;
            if (b != null && AutomationProperties.GetName(b) == "恢复自动排序") return b;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                Button found = ResetButton(VisualTreeHelper.GetChild(node, i));
                if (found != null) return found;
            }
            return null;
        }
        static Border Tile(PanelWindow panel, string path)
        {
            int i = panel.TestRenderedIndexOfPath(path);
            return i < 0 ? null : panel.TestRenderedTileSnapshot()[i];
        }
        [STAThread]
        static int Main()
        {
            string root = Path.Combine(Path.GetTempPath(), "CloverCommonRules-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "config"));
            Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            PanelWindow panel = null;
            try
            {
                long now = DateTime.UtcNow.Ticks;
                AppItem fixture = new AppItem { Name = "Code", Path = "code.exe", Usage = 3, LastUsed = now };
                Check(PanelWindow.IsCommonByUsage(fixture, 0, now), "刚启动的用户应用进入常用");
                fixture.LastUsed = now - UsageStore.RecentWindowTicks + 1;
                Check(PanelWindow.IsCommonByUsage(fixture, 0, now), "72 小时边界前仍显示");
                fixture.LastUsed--;
                Check(!PanelWindow.IsCommonByUsage(fixture, 0, now), "恰好连续 72 小时未用就移除");
                fixture.LastUsed = now + 1;
                Check(!PanelWindow.IsCommonByUsage(fixture, 0, now), "异常未来时间不进入常用");
                fixture.LastUsed = now; fixture.LowValue = true;
                Check(!PanelWindow.IsCommonByUsage(fixture, 0, now), "旧低价值布尔标记也不进入常用");
                fixture.LowValue = false; fixture.Role = AppRole.LowValue;
                Check(!PanelWindow.IsCommonByUsage(fixture, 0, now), "低价值即使使用频繁也不进入");
                fixture.Role = AppRole.Computer;
                Check(!PanelWindow.IsCommonByUsage(fixture, 0, now), "电脑用的只保留搜索");
                fixture.Role = AppRole.User; fixture.Hidden = true;
                Check(!PanelWindow.IsCommonByUsage(fixture, 0, now), "手动隐藏不能从常用重新出现");
                fixture.Hidden = false; fixture.ExcludedEntry = true;
                Check(!PanelWindow.IsCommonByUsage(fixture, 0, now), "非应用排除入口不进入");
                fixture.ExcludedEntry = false;
                Check(!PanelWindow.IsCommonByUsage(fixture, now, now), "手动移除快照生效");
                Check(PanelWindow.IsCommonByUsage(fixture, now - 1, now), "再次启动后可重新进入");
                fixture.LastUsed = 0;
                Check(!PanelWindow.IsCommonByUsage(fixture, 0, now), "缺少最后启动时间不冒充近期应用");
                Check(!PanelWindow.IsCommonByUsage(null, 0, now), "空入口安全过滤");
                Check(PanelWindow.CommonMenuFor(false, 3) == PanelWindow.CommonMenuAction.None,
                    "旧固定状态不产生固定或取消固定菜单");

                List<object> entries = new List<object>();
                string[] paths = new string[7];
                for (int i = 0; i < paths.Length; i++)
                {
                    paths[i] = Path.Combine(root, "app" + i + ".exe");
                    File.WriteAllText(paths[i], "fixture");
                    entries.Add(new Dictionary<string, object> { { "name", "用户应用 " + i }, { "path", paths[i] }, { "category", "其他" } });
                }
                File.WriteAllText(Path.Combine(root, "config", "apps.json"), new JavaScriptSerializer().Serialize(
                    new Dictionary<string, object> { { "apps", entries } }));
                Settings settings = Settings.Load(root);
                settings.PinnedCommon.Add(paths[2]);
                settings.PinnedCommon.Add(paths[3]);
                settings.PinnedCommon.Add(paths[4]);
                settings.LowValueMarked.Add(paths[2]);
                settings.ComputerAppRoles.Add(paths[5]);
                settings.Save();
                HiddenApps.Load(root).Hide(paths[6]);
                UsageStore usage = UsageStore.Load(root);
                usage.Record(paths[0], now - 2 * TimeSpan.TicksPerDay);
                usage.Record(paths[0], now - TimeSpan.TicksPerDay);
                usage.Record(paths[0], now);
                for (int i = 0; i < 12; i++) usage.Record(paths[1], now);
                usage.Record(paths[2], now);
                usage.Record(paths[3], now - 4 * TimeSpan.TicksPerDay);
                usage.Record(paths[5], now); usage.Record(paths[6], now);
                Check(usage.GetRecentDays(paths[0], now) == 3 && usage.GetRecentDays(paths[1], now) == 1,
                    "近期持续使用与一次集中启动可区分");
                Check(usage.GetRecentCount(paths[0], now) == 3 && usage.GetRecentCount(paths[1], now) == 12,
                    "最近三天启动次数单独统计");
                Check(usage.GetRecentCount(paths[3], now) == 0 && usage.GetCount(paths[3]) == 1,
                    "过期记录不计近期，但累计历史保留");
                UsageStore reloaded = UsageStore.Load(root);
                Check(reloaded.GetRecentCount(paths[0], now) == 3 && reloaded.GetRecentDays(paths[0], now) == 3,
                    "近期频率与持续天数保存后往返");
                string legacyRoot = Path.Combine(root, "legacy");
                Directory.CreateDirectory(Path.Combine(legacyRoot, "config"));
                File.WriteAllText(Path.Combine(legacyRoot, "config", "usage.json"), new JavaScriptSerializer().Serialize(
                    new Dictionary<string, object> { { "apps", new Dictionary<string, object> {
                        { "legacy.exe", new Dictionary<string, object> { { "count", 999 }, { "last", now } } } } } }));
                UsageStore legacy = UsageStore.Load(legacyRoot);
                Check(legacy.GetCount("legacy.exe") == 999 && legacy.GetRecentCount("legacy.exe", now) == 1,
                    "旧累计次数不伪造近期频率，保留最后一次启动");

                panel = new PanelWindow(root, null, Settings.Load(root));
                panel.Width = panel.MinWidth; panel.Show(); panel.TestSwitchCategory("常用"); Wait(panel);
                Check(panel.TestClassificationOf(paths[2]) == AppRole.LowValue, "旧固定不覆盖低价值分类");
                Check(panel.TestRenderedTileSnapshot().Count == 2 && panel.TestFirstRenderedPath() == paths[0],
                    "统一常用仅有近期用户应用，持续使用优先于单日高频");
                Check(!Text(panel, "手动固定") && !Text(panel, "最近常用") && Text(panel, "自动排序"),
                    "没有固定与最近分组，明确显示自动排序");
                Button reset = ResetButton(panel);
                Check(reset != null && !reset.IsEnabled && reset.ActualWidth > 0,
                    "窄窗口恢复按钮可见，自动状态不可重复恢复");
                Border kept = Tile(panel, paths[0]);
                panel.TestCommitDrag(0, 2);
                Check(panel.TestFirstRenderedPath() == paths[1] && Text(panel, "手动排序"),
                    "统一列表拖动立即切换手动排序");
                Check(ReferenceEquals(kept, Tile(panel, paths[0])) && panel.TestTileMapConsistent(),
                    "拖动不刷新磁贴图标和映射");
                Check(ResetButton(panel).IsEnabled, "手动状态可以恢复自动排序");
                panel.TestExpireCommon(paths[0]);
                Check(Tile(panel, paths[0]) == null && Tile(panel, paths[1]) != null && Text(panel, "手动排序"),
                    "没有新启动也检测过期，手动排序不豁免三天规则");
                Check(UsageStore.Load(root).GetCount(paths[0]) == 3, "过期检查不改写真实启动历史");
                panel.TestRecordAppUsage(paths[0]); panel.TestSwitchCategory("常用"); Wait(panel);
                panel.Close(); panel = new PanelWindow(root, null, Settings.Load(root));
                panel.Show(); panel.TestSwitchCategory("常用"); Wait(panel);
                Check(panel.TestFirstRenderedPath() == paths[1] && Text(panel, "手动排序"),
                    "手动模式与顺序重启后保持");
                kept = Tile(panel, paths[0]);
                ResetButton(panel).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(panel.TestFirstRenderedPath() == paths[0] && Text(panel, "自动排序"),
                    "恢复按钮走生产动作重新使用近期排序");
                Check(ReferenceEquals(kept, Tile(panel, paths[0])) && panel.TestTileMapConsistent(),
                    "恢复自动排序复用现有磁贴");
                Check(UsageStore.Load(root).GetCount(paths[1]) == 12, "恢复不删除真实启动历史");
                panel.TestDismissCommon(paths[0]);
                Check(Tile(panel, paths[0]) == null && UsageStore.Load(root).GetCount(paths[0]) == 4,
                    "从常用移除仅影响本页，历史仍保留");
                panel.TestRecordAppUsage(paths[0]); panel.TestSwitchCategory("常用"); Wait(panel);
                Check(Tile(panel, paths[0]) != null, "再次从四叶草启动后自然重入常用");
                panel.TestSwitchCategory(""); Wait(panel);
                panel.TestOrder(new List<string> { paths[4], paths[1], paths[0], paths[3] });
                panel.TestSwitchCategory("常用"); Wait(panel);
                panel.TestCommitDrag(0, 2); panel.TestRestoreCommonAutoSort();
                panel.TestSwitchCategory(""); Wait(panel);
                Check(panel.TestFirstRenderedPath() == paths[4], "常用恢复不改变全部页个人顺序");
                Check(Tile(panel, paths[3]) != null && Tile(panel, paths[4]) != null,
                    "过期与未启动应用仍留在全部，不隐藏软件");
                panel.TestSwitchCategory("常用"); Wait(panel);
                for (int i = 0; i < 6; i++) panel.TestRecordAppUsage(paths[2]);
                panel.TestSwitchCategory("常用"); Wait(panel);
                Check(Tile(panel, paths[2]) == null, "低价值即使再次启动也不会回常用");
                panel.Close(); panel = new PanelWindow(root, null, Settings.Load(root));
                panel.Show(); panel.TestSwitchCategory("常用"); Wait(panel);
                Check(Text(panel, "自动排序") && panel.TestFirstRenderedPath() == paths[0],
                    "恢复自动状态重启后保持");
                Ui.ApplyTheme(Ui.ThemeMode.Light);
                Check(Ui.TileHover.Color.A < 255 && Ui.TilePressed.Color.A > Ui.TileHover.Color.A,
                    "浅色磁贴交互仍保持半透明");
                Ui.ApplyTheme(Ui.ThemeMode.Dark);
                Check(Ui.TileHover.Color.A < 255 && Ui.TilePressed.Color.A > Ui.TileHover.Color.A,
                    "深色磁贴交互仍保持半透明");
                Ui.ApplyTheme(Ui.ThemeMode.Light);
            }
            catch (Exception ex) { Check(false, "异常：" + ex); }
            finally
            {
                if (panel != null) panel.Close();
                app.Shutdown();
                try { Directory.Delete(root, true); } catch { }
            }
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            return failed == 0 ? 0 : 1;
        }
    }
}
