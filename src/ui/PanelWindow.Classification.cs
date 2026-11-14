using System;
using System.Collections.Generic;
using System.Windows;

namespace Clover
{
    partial class PanelWindow
    {
        AppItem FindSearchApp(SearchResult result)
        {
            if (result == null || catalog == null) return null;
            AppItem exact = FindAppByPath(result.Path);
            if (exact != null) return exact;
            string target = ShortcutResolver.ResolveDedupKey(result.Path);
            foreach (AppItem app in catalog.Apps)
                if (app != null && !string.IsNullOrEmpty(target)
                    && string.Equals(target, ShortcutResolver.ResolveDedupKey(app.Path), StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(app.Name, result.Name, StringComparison.OrdinalIgnoreCase)) return app;
                }
            // 相同宿主不代表相同入口，即使当前目录里只收录了其中一个功能。
            // 名字仅供无身份路径结果兜底；不会用它覆盖同一软件的不同功能入口。
            foreach (AppItem app in catalog.Apps)
                if (app != null && string.IsNullOrEmpty(target)
                    && string.Equals(app.Name, result.Name, StringComparison.OrdinalIgnoreCase)) return app;
            return null;
        }

        void ClassifySearchResult(SearchResult result, AppRole role)
        {
            AppItem app = FindSearchApp(result);
            if (app == null && catalog != null && result != null && !string.IsNullOrWhiteSpace(result.Path))
            {
                app = new AppItem { Name = result.Name, Path = result.Path, Category = "其他" };
                if (!Catalog.RegisterManual(baseDir, app))
                {
                    MessageBox.Show(this, "分类未保存：无法登记应用入口，原清单已保留。", AppBrand.Text("四叶草"));
                    return;
                }
                catalog.Apps.Add(app);
                CatalogPreloader.Invalidate();
            }
            if (app != null) SetClassification(app, role);
        }

        int SearchRoleOf(SearchResult result)
        {
            AppItem app = FindSearchApp(result);
            return app == null ? (appClassification == null ? 0 : (int)appClassification.RoleOf(result.Path)) : (int)app.Role;
        }

        void SetClassification(AppItem app, AppRole role, bool refresh = true)
        {
            if (app == null || settings == null) return;
            foreach (List<string> list in new[] { settings.ActiveAppRoles, settings.AuxiliaryAppRoles,
                settings.ComputerAppRoles, settings.LowValueMarked, settings.LowValueUnmarked })
                list.RemoveAll(p => HiddenApps.Normalize(p) == HiddenApps.Normalize(app.Path));
            if (role == AppRole.User) { settings.ActiveAppRoles.Add(app.Path); settings.LowValueUnmarked.Add(app.Path); }
            else if (role == AppRole.LowValue) settings.LowValueMarked.Add(app.Path);
            else
            {
                settings.ComputerAppRoles.Add(app.Path);
                hiddenApps.RemoveException(app.Path);
            }
            if (role != AppRole.Computer && hiddenApps.IsException(app.Path)) hiddenApps.RemoveException(app.Path);
            if (appClassification == null) appClassification = new AppClassification(baseDir);
            appClassification.Set(app, role);
            settings.Save();
            appClassification.Apply(catalog.Apps, settings, hiddenApps);
            if (!refresh) return;
            RefreshNavCountsInPlace();
            string query = searchBox == null ? "" : searchBox.Text.Trim();
            if (query.Length > 0 && !IsDeskTab(current) && current != FilesCategory)
            { searchResults.Search(query); renderedSig = ""; return; }
            if (IsDeskTab(current) || current == FilesCategory) { renderedSig = ""; return; }
            if (!TryRefreshCurrentTilesInPlace()) RenderApps();
        }

        int VisibleAppCount()
        { return catalog == null ? 0 : catalog.Apps.FindAll(a => !a.Hidden && !a.ExcludedEntry && a.Role != AppRole.Computer).Count; }

        internal void TestClassification(string path, AppRole role) { SetClassification(FindAppByPath(path), role); }
        internal AppRole TestClassificationOf(string path) { return FindAppByPath(path).Role; }
        internal bool TestSearchAllowed(SearchResult result) { return searchResults.ResultAllowed(result); }
        internal int TestSearchRole(SearchResult result) { return SearchRoleOf(result); }
        internal void TestClassifySearch(SearchResult result, AppRole role) { ClassifySearchResult(result, role); }
    }
}
