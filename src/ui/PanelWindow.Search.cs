using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Clover
{
    partial class PanelWindow
    {
        // 分类右键菜单（重命名 / 上移 / 下移）已随分类系统下线移除。
        // 界面只剩「常用」「全部」两个虚拟标签，它们不提供改名/排序。
        // Categories.cs 因此失去全部调用方，已一并删除。

        void OnSearchChanged()
        {
            // 程序自己清空搜索框时不重建（打开面板的场景）
            if (searchSuppress) return;
            if (searchHint != null)
                searchHint.Visibility = searchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

            // 桌面收纳已载入时只切现有图标的 Visibility，不重新列目录、清选择或写坐标。
            if (IsDeskTab(current) && deskView != null)
            {
                deskView.ApplyFilter(searchBox.Text);
                return;
            }
            if (current == FilesCategory && filesView != null)
            {
                filesView.ApplyFilter(searchBox.Text);
                return;
            }
            unchecked { searchGeneration++; }
            // 输入变化即取消旧结果和后台回调，不能在防抖窗口内启动上一搜索的应用。
            if (searchResults != null) searchResults.Invalidate();
            int ownGeneration = searchGeneration;
            if (searchDebounce == null)
            {
                searchDebounce = new DispatcherTimer(DispatcherPriority.Input, Dispatcher);
                searchDebounce.Interval = TimeSpan.FromMilliseconds(100);
                searchDebounce.Tick += OnSearchDebounce;
            }
            searchDebounce.Stop();
            searchDebounceGeneration = ownGeneration;
            searchDebounce.Start();
        }


        void OnSearchDebounce(object sender, EventArgs e)
        {
            if (searchDebounce == null) return;
            searchDebounce.Stop();
            if (searchDebounceGeneration == searchGeneration) RenderApps();
        }


        void UpdateSearchFooter()
        {
            if (current == FilesCategory || IsDeskTab(current)) return;
            if (footerCount == null || searchResults == null || searchBox == null
                || searchBox.Text.Trim().Length == 0) return;
            footerCount.ToolTip = null;
            int n = searchResults.ShownCount;
            footerCount.Text = n > 0
                ? "找到 " + n + " 条结果"
                : (searchResults.Completed ? "没有结果" : "搜索中…");
        }


        /// <summary>测试用：设置搜索框文字，走与用户输入相同的 TextChanged 路径。</summary>
        internal void TestSetSearchText(string text)
        {
            try
            {
                if (searchBox != null) searchBox.Text = text ?? "";
            }
            catch (Exception ex) { Ui.LogError(ex, "测试：设置搜索文字"); }
        }


        /// <summary>测试用：当前页面的搜索提示。</summary>
        internal string TestSearchHint()
        {
            try { return searchHint == null ? "" : (searchHint.Text ?? ""); }
            catch { return ""; }
        }


        /// <summary>测试用：当前页面的底部快捷提示。</summary>
        internal string TestFooterHint()
        {
            try { return footerHint == null ? "" : (footerHint.Text ?? ""); }
            catch { return ""; }
        }


        /// <summary>测试用：全局搜索结果面板是否可见。</summary>
        internal bool TestSearchResultVisible()
        {
            try
            {
                return searchResultPanel != null
                    && searchResultPanel.Visibility == Visibility.Visible;
            }
            catch { return false; }
        }


        internal int TestSearchResultCount()
        {
            try { return searchResults == null ? 0 : searchResults.ShownCount; }
            catch { return 0; }
        }


        internal bool TestSearchCompleted()
        {
            try { return searchResults != null && searchResults.Completed; }
            catch { return false; }
        }


        /// <summary>测试用：读搜索框文字。</summary>
        internal string TestSearchText()
        {
            try { return searchBox == null ? "" : (searchBox.Text ?? ""); }
            catch { return ""; }
        }
    }
}
