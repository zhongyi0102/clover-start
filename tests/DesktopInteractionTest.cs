using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Clover
{
    static class DesktopInteractionTest
    {
        static int passed, failed;
        static void Check(bool ok, string name)
        {
            if (ok) passed++; else failed++;
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name);
        }

        [STAThread]
        static int Main()
        {
            string root = Path.Combine(Path.GetTempPath(), "CloverDesktopInteraction-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Application application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            DesktopView view = null;
            try
            {
                Ui.ApplyTheme(Ui.ThemeMode.Light);
                string exe = Path.Combine(root, "tool.exe");
                string note = Path.Combine(root, "note.txt");
                string installer = Path.Combine(root, "setup.msi");
                File.WriteAllText(exe, "exe"); File.WriteAllText(note, "text"); File.WriteAllText(installer, "msi");
                Check(DesktopContentRules.Classify(exe) == DesktopContentKind.Application, "应用入口归面板");
                Check(DesktopContentRules.Classify(installer) == DesktopContentKind.NonApplication, "安装包归桌面内容");
                Check(DesktopContentRules.Classify(note) == DesktopContentKind.NonApplication, "文档归桌面内容");
                Check(DesktopContentRules.Classify(Path.Combine(root, "missing.lnk")) == DesktopContentKind.Unknown, "未知入口安全保留");
                string appLink = Path.Combine(root, "application.lnk");
                string docLink = Path.Combine(root, "document.lnk");
                string folderLink = Path.Combine(root, "folder.lnk");
                MakeLink(appLink, exe, "--project sample");
                MakeLink(docLink, note, "");
                MakeLink(folderLink, root, "");
                Check(DesktopContentRules.Classify(appLink) == DesktopContentKind.Application, "应用快捷方式不进收纳");
                Check(DesktopContentRules.Classify(docLink) == DesktopContentKind.NonApplication, "文件快捷方式可收纳");
                Check(DesktopContentRules.Classify(folderLink) == DesktopContentKind.NonApplication, "文件夹快捷方式可收纳");
                Check(DeskCollector.IsAllowed(docLink) == DeskImporter.IsAllowed(docLink), "批量收纳和拖入用同一规则");
                string web = Path.Combine(root, "website.url");
                File.WriteAllText(web, "[InternetShortcut]\r\nURL=https://example.com\r\n");
                Check(DesktopContentRules.Classify(web) == DesktopContentKind.NonApplication, "普通网页链接不是应用");
                string copiedLink = Path.Combine(root, "copy.lnk");
                Check(VerifiedFileMove.Copy(appLink, copiedLink).Ok
                    && DesktopContentRules.ShortcutProperty(copiedLink, "Arguments") == "--project sample",
                    "应用入口复制保留启动参数");

                string data = Path.Combine(root, "data");
                Directory.CreateDirectory(Path.Combine(data, "config"));
                Settings settings = Settings.Load(data);
                settings.ActiveAppRoles.Add(exe.ToUpperInvariant());
                settings.AuxiliaryAppRoles.Add(appLink);
                settings.CollapseAuxiliaryApps = true;
                settings.Save();
                Settings restored = Settings.Load(data);
                Check(restored.CollapseAuxiliaryApps && restored.ActiveAppRoles.Count == 1
                    && restored.AuxiliaryAppRoles.Count == 1, "应用角色和折叠状态独立持久化");
                AppWatcher appWatcher = new AppWatcher(data, null, restored);
                Check(!appWatcher.PreserveDesktopApplication(appLink), "不在面板的应用快捷方式保留原处");
                Catalog.RegisterManual(data, new AppItem { Name = "tool", Path = appLink, Category = "其他" });
                Check(appWatcher.PreserveDesktopApplication(appLink), "完整应用快捷方式交接可从目录回读");
                Check(appWatcher.PreserveDesktopApplication(appLink)
                    && Directory.GetFiles(Path.Combine(data, "config", "desktop-app-entries")).Length == 1,
                    "重复交接不产生多份入口");
                DesktopCleaner cleaner = new DesktopCleaner(data, restored);
                cleaner.EnsureApplicationEntry = delegate(string source) { File.AppendAllText(source, "changed"); return true; };
                string changing = Path.Combine(root, "changing.url");
                File.WriteAllText(changing, "[InternetShortcut]\r\nURL=steam://rungameid/1\r\n");
                object removed = new System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>>();
                bool processed = (bool)typeof(DesktopCleaner).GetMethod("ProcessOne", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(cleaner, new object[] { changing, removed });
                Check(!processed && File.Exists(changing), "交接中入口变化必须保留源，不进入删除路径");

                VirtualDesks desks = new VirtualDesks(data);
                desks.Load();
                desks.Root = VirtualDesks.DefaultRoot(data);
                desks.EnsureDesk("桌面文件");
                string dir = desks.FolderOf("桌面文件");
                File.WriteAllText(Path.Combine(dir, "first.txt"), "one");
                File.WriteAllText(Path.Combine(dir, "second.txt"), "two");
                Canvas canvas = new Canvas { Width = 640, Height = 400 };
                view = new DesktopView(null, canvas, new Canvas(), desks, "桌面文件");
                view.Load("桌面文件");
                Border first = view.TestTile("first.txt"), second = view.TestTile("second.txt");
                Check(first != null && second != null, "两个桌面项目可见");
                Check(Canvas.GetTop(first) != Canvas.GetTop(second)
                    || Canvas.GetLeft(first) != Canvas.GetLeft(second), "新项目不重叠且空位准确");
                view.SelectAllVisible();
                view.ApplyFilter("first");
                view.HandleKey(Key.A, ModifierKeys.Control);
                Check(view.SelectedPaths().Count == 1 && view.SelectedPaths()[0].EndsWith("first.txt"),
                    "搜索全选只操作可见项");
                view.HandleKey(Key.Right, ModifierKeys.None);
                Check(view.SelectedPaths().Count == 1 && view.SelectedPaths()[0].EndsWith("first.txt"),
                    "方向键不选中筛选外的项目");
                view.Load("桌面文件");
                Check(ReferenceEquals(first, view.TestTile("first.txt"))
                    && ReferenceEquals(second, view.TestTile("second.txt")), "同页更新复用全部图标控件");
                Check(view.SelectedPaths().Count == 1, "更新不丢可见选择");
                Check(second.Visibility == Visibility.Collapsed, "更新保留搜索筛选");
                view.ApplyFilter("");
                double secondX = Canvas.GetLeft(second), secondY = Canvas.GetTop(second);
                File.WriteAllText(Path.Combine(dir, "aaa.txt"), "new before existing");
                view.Load("桌面文件");
                Border added = view.TestTile("aaa.txt");
                Check(Canvas.GetLeft(second) == secondX && Canvas.GetTop(second) == secondY
                    && (Canvas.GetLeft(added) != Canvas.GetLeft(first) || Canvas.GetTop(added) != Canvas.GetTop(first))
                    && (Canvas.GetLeft(added) != secondX || Canvas.GetTop(added) != secondY),
                    "名称排前的新文件不抢旧图标位置");
                desks.SetPos("桌面文件", "first.txt", 980, 620);
                view.Load("桌面文件");
                Check(canvas.MinWidth >= 980 && canvas.MinHeight >= 620, "画布扩展可访问视口外的图标");
                view.ArrangeAll();
                view.HandleKey(Key.Z, ModifierKeys.Control);
                Check(Canvas.GetLeft(first) == 980 && Canvas.GetTop(first) == 620, "撤销排列恢复自由布局");
                view.ArrangeAll();
                typeof(DesktopView).GetField("menuOpen", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(view, true);
                view.HandleKey(Key.Z, ModifierKeys.Control);
                Check(Canvas.GetLeft(first) == 980 && Canvas.GetTop(first) == 620, "右键菜单打开时也能撤销摆放");
                typeof(DesktopView).GetField("menuOpen", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(view, false);
                File.Move(Path.Combine(dir, "first.txt"), Path.Combine(dir, "renamed.txt"));
                Pump(700);
                Border renamed = view.TestTile("renamed.txt");
                Check(renamed != null && Canvas.GetLeft(renamed) == 980 && Canvas.GetTop(renamed) == 620,
                    "外部改名自动更新并继承坐标");
                Check(ReferenceEquals(second, view.TestTile("second.txt")), "外部改名不重建其他图标");
                File.WriteAllText(Path.Combine(dir, "new.txt"), "new");
                Pump(600);
                Check(view.TestTile("new.txt") != null && ReferenceEquals(second, view.TestTile("second.txt")),
                    "外部新增增量出现");
            }
            catch (Exception ex) { Check(false, "异常：" + ex); }
            finally
            {
                if (view != null) view.Dispose();
                application.Shutdown();
                try { Directory.Delete(root, true); } catch { }
            }
            Console.WriteLine("通过 " + passed + " 项，失败 " + failed + " 项");
            return failed == 0 ? 0 : 1;
        }

        static void MakeLink(string path, string target, string arguments)
        {
            object shell = null, shortcut = null;
            try
            {
                Type type = Type.GetTypeFromProgID("WScript.Shell");
                shell = Activator.CreateInstance(type);
                shortcut = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                Type sc = shortcut.GetType();
                sc.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { target });
                sc.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, new object[] { arguments });
                sc.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            }
            finally
            {
                if (shortcut != null) Marshal.ReleaseComObject(shortcut);
                if (shell != null) Marshal.ReleaseComObject(shell);
            }
        }

        static void Pump(int milliseconds)
        {
            DispatcherFrame frame = new DispatcherFrame();
            DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            timer.Tick += delegate { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
    }
}
