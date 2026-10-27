using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Clover
{
    /// <summary>
    /// 分类规则表测试（config\category-rules.tsv）。
    ///
    /// 覆盖四件事：
    ///   1. 优先级：手工覆盖 > 规则表 > 内置规则
    ///   2. 匹配模式：word 词边界 / exact / sub / prefix
    ///   3. 归一化：全角折叠、大小写、标点处理
    ///   4. 健壮性：文件缺失自动生成、损坏回退、格式错误跳过、热加载
    /// </summary>
    static class CategoryRulesTest
    {
        static int failed = 0;

        static void Check(bool cond, string name, string detail = "")
        {
            Console.WriteLine((cond ? "PASS  " : "FAIL  ") + name + (cond || detail.Length == 0 ? "" : "  [" + detail + "]"));
            if (!cond) failed++;
        }

        static void Eq(string got, string want, string name)
        {
            Check(got == want, name, "实际=" + got + " 期望=" + want);
        }

        static string NewRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "clover-rules-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "config"));
            return root;
        }

        static void WriteRules(string root, string text)
        {
            File.WriteAllText(CategoryRules.PathOf(root), text, new UTF8Encoding(false));
        }

        static void WriteAppsStub(string root)
        {
            File.WriteAllText(Path.Combine(root, "config", "apps.json"),
                "{\"order\":[],\"apps\":[]}", new UTF8Encoding(false));
        }

        /// <summary>用一组规则建临时目录并加载。</summary>
        static string Load(string rulesText)
        {
            string root = NewRoot();
            WriteRules(root, rulesText);
            WriteAppsStub(root);
            Catalog.Load(root);
            return root;
        }

        static void Main()
        {
            // ================= 1. 手动覆盖表优先级最高 =================
            {
                string root = NewRoot();
                try
                {
                    // 规则表说「开发」，覆盖表说「办公」→ 应取办公
                    WriteRules(root, "开发\tword\t测试应用abc\n");
                    string exe = Path.Combine(root, "t.exe");
                    File.WriteAllText(exe, "x", new UTF8Encoding(false));

                    string manual = "{\"apps\":[{\"name\":\"测试应用abc\",\"path\":\"" +
                        exe.Replace("\\", "\\\\") + "\",\"category\":\"其他\"}]}";
                    File.WriteAllText(Path.Combine(root, "config", "manual-apps.json"), manual, new UTF8Encoding(false));

                    // 必须先 Load 一次，规则表才会被解析（Init 在 Catalog.Load 里）
                    Catalog.Load(root);
                    // 先确认规则表本身能命中
                    Eq(Catalog.GuessCategory("测试应用abc"), "开发", "规则表命中 -> 开发");

                    // 再加手工覆盖
                    CategoryOverrides.Load(root).Set(exe, "办公");
                    Catalog c = Catalog.Load(root);
                    string got = null;
                    foreach (AppItem a in c.Apps) if (a.Name == "测试应用abc") got = a.Category;
                    Eq(got, "办公", "手工覆盖 > 规则表（覆盖表赢）");
                }
                finally { try { Directory.Delete(root, true); } catch { } }
            }

            // ================= 2. 规则表优先于内置规则 =================
            {
                // 内置规则会把 "Notepad" 判成「其他」；规则表改判「办公」
                string root = Load("办公\tword\tnotepad\n");
                try
                {
                    Eq(Catalog.GuessCategory("Notepad"), "办公", "规则表 > 内置规则");
                    // 归一化会把 `++` 变成空格再裁掉，所以 Notepad++ 等价于 notepad，命中是**正确**行为
                    Eq(Catalog.GuessCategory("Notepad++"), "办公", "Notepad++ 归一化后等同 notepad");
                }
                finally { try { Directory.Delete(root, true); } catch { } }
            }
            {
                // 反过来验证：规则表没命中时，内置规则照常工作（不退化）
                string root = Load("# 空表\n");
                try
                {
                    Eq(Catalog.GuessCategory("Google Chrome"), "网络通讯", "空规则表 -> 内置规则仍生效（Chrome）");
                    Eq(Catalog.GuessCategory("ChromeDriver"), "工具/命令行", "空规则表 -> 内置规则仍生效（驱动）");
                    Eq(Catalog.GuessCategory("HP Scan"), "系统工具", "空规则表 -> 内置规则仍生效（HP）");
                    Eq(Catalog.GuessCategory("大肥鱼桌宠"), "其他", "空规则表 -> 未知仍是其他");
                }
                finally { try { Directory.Delete(root, true); } catch { } }
            }

            // ================= 3. 匹配模式 =================
            {
                string root = Load(
                    "开发\tword\talpha\n" +
                    "办公\texact\tbeta\n" +
                    "网络通讯\tsub\tgamma\n" +
                    "影音娱乐\tprefix\tdelta\n");
                try
                {
                    // word：英文要词边界
                    Eq(Catalog.GuessCategory("Alpha Tool"), "开发", "word 命中独立词");
                    Eq(Catalog.GuessCategory("alpha"), "开发", "word 命中整串");
                    Eq(Catalog.GuessCategory("Alphabet"), "其他", "word 不命中前缀拼接（Alphabet）");
                    Eq(Catalog.GuessCategory("Alpha-beta"), "开发", "word 命中连字符后（- 不算字母）");

                    // exact：归一化后完全相等
                    Eq(Catalog.GuessCategory("Beta"), "办公", "exact 命中相同");
                    Eq(Catalog.GuessCategory(" Beta "), "办公", "exact 命中（前后空白无所谓）");
                    Eq(Catalog.GuessCategory("Beta Tool"), "其他", "exact 不命中更长的名字");

                    // sub：包含即可
                    Eq(Catalog.GuessCategory("My Gamma App"), "网络通讯", "sub 命中中间");
                    Eq(Catalog.GuessCategory("gammatool"), "网络通讯", "sub 命中拼接");

                    // prefix：开头
                    Eq(Catalog.GuessCategory("Delta Force"), "影音娱乐", "prefix 命中开头");
                    Eq(Catalog.GuessCategory("My Delta"), "其他", "prefix 不命中中间");
                }
                finally { try { Directory.Delete(root, true); } catch { } }
            }

            // ================= 4. 一行多别名 + 注释 + 空行 =================
            {
                string root = Load(
                    "# 这是注释\n" +
                    "\n" +
                    "办公\tword\tfoo|bar|baz\n");
                try
                {
                    Eq(Catalog.GuessCategory("Foo"), "办公", "一行多别名：第 1 个");
                    Eq(Catalog.GuessCategory("Bar Tool"), "办公", "一行多别名：第 2 个");
                    Eq(Catalog.GuessCategory("Baz"), "办公", "一行多别名：第 3 个");
                }
                finally { try { Directory.Delete(root, true); } catch { } }
            }

            // ================= 5. 长别名优先 =================
            {
                string root = Load(
                    "办公\tword\tvisual studio\n" +
                    "开发\tword\tvisual studio code\n");
                try
                {
                    // 两个别名都是「visual studio」开头，长的应先命中
                    Eq(Catalog.GuessCategory("Visual Studio Code"), "开发", "长别名优先（VS Code -> 开发）");
                    Eq(Catalog.GuessCategory("Visual Studio"), "办公", "短别名仍可用（VS -> 办公）");
                }
                finally { try { Directory.Delete(root, true); } catch { } }
            }

            // ================= 6. 归一化：全角 / 大小写 / 标点 =================
            {
                string root = Load("办公\tword\tword\n");
                try
                {
                    Eq(Catalog.GuessCategory("Word"), "办公", "首字母大写命中");
                    Eq(Catalog.GuessCategory("WORD"), "办公", "全大写命中");
                    Eq(Catalog.GuessCategory("Ｗｏｒｄ"), "办公", "全角字符命中（FormKC 折叠）");
                    Eq(Catalog.GuessCategory("Microsoft Word"), "办公", "组合词命中");
                }
                finally { try { Directory.Delete(root, true); } catch { } }
            }
            {
                // 标点：7-Zip 在规则表里写 7zip（canonical) 或用 word 模式
                string root = Load("系统工具\tword\t7 zip\n");
                try
                {
                    Eq(Catalog.GuessCategory("7-Zip"), "系统工具", "连字符归一化为空格后命中");
                }
                finally { try { Directory.Delete(root, true); } catch { } }
            }

            // ================= 7. 中文别名按子串 =================
            {
                string root = Load("网络通讯\tword\t微信\n");
                try
                {
                    Eq(Catalog.GuessCategory("微信"), "网络通讯", "中文整串命中");
                    Eq(Catalog.GuessCategory("微信电脑版"), "网络通讯", "中文按子串命中（无词边界要求）");
                }
                finally { try { Directory.Delete(root, true); } catch { } }
            }

            // ================= 8. 文件缺失：自动生成默认表 =================
            {
                string root = NewRoot();
                try
                {
                    string path = CategoryRules.PathOf(root);
                    Check(!File.Exists(path), "初始状态没有规则表文件");

                    WriteAppsStub(root);
                    Catalog.Load(root);

                    Check(File.Exists(path), "首次加载会自动生成默认规则表");
                    string text = File.ReadAllText(path, Encoding.UTF8);
                    Check(text.Contains("# 四叶草启动面板"), "生成的文件含表头说明");
                    Check(text.Contains("黑神话"), "生成的表含默认条目（游戏别名）");
                    Check(CategoryRules.RuleCount > 100, "默认表解析出足够多的规则", "实际 " + CategoryRules.RuleCount);

                    // 不覆盖用户已有文件
                    string custom = "# 用户自定义\n办公\tword\tzzz\n";
                    WriteRules(root, custom);
                    Catalog.Load(root);
                    Check(File.ReadAllText(path, Encoding.UTF8) == custom, "已有文件不被默认表覆盖");
                }
                finally { try { Directory.Delete(root, true); } catch { } }
            }

            // ================= 9. 损坏 / 格式错误：跳过而不是崩 =================
            {
                string root = Load(
                    "这一行没有制表符\n" +
                    "办公\tinvalid_mode\txxx\n" +
                    "\tword\t没有分类\n" +
                    "开发\tword\t\n" +              // 别名空
                    "办公\tword\tgood\n");
                try
                {
                    Eq(Catalog.GuessCategory("Good"), "办公", "格式错误行被跳过后，正确行仍生效");
                    // 内置规则不受影响
                    Eq(Catalog.GuessCategory("Google Chrome"), "网络通讯", "坏文件下内置规则照常工作");
                }
                finally { try { Directory.Delete(root, true); } catch { } }
            }

            // ================= 10. 空表 / 只有注释 =================
            {
                string root = Load("");
                try
                {
                    Eq(Catalog.GuessCategory("Google Chrome"), "网络通讯", "空文件 -> 内置规则照常");
                }
                finally { try { Directory.Delete(root, true); } catch { } }
            }
            {
                string root = Load("# 只有注释\n# 再来一行\n");
                try
                {
                    Eq(Catalog.GuessCategory("Google Chrome"), "网络通讯", "只有注释 -> 内置规则照常");
                }
                finally { try { Directory.Delete(root, true); } catch { } }
            }

            // ================= 11. 热加载：改了文件重新加载就生效 =================
            {
                string root = NewRoot();
                try
                {
                    WriteRules(root, "办公\tword\thotload\n");
                    WriteAppsStub(root);
                    Catalog.Load(root);
                    Eq(Catalog.GuessCategory("HotLoad"), "办公", "热加载前：办公");

                    // 改文件，注意要更新时间戳（不同文件系统精度不同，这里等一小会）
                    System.Threading.Thread.Sleep(1100);
                    WriteRules(root, "开发\tword\thotload\n");
                    Catalog.Load(root);
                    Eq(Catalog.GuessCategory("HotLoad"), "开发", "热加载后：开发（无需重启）");
                }
                finally { try { Directory.Delete(root, true); } catch { } }
            }

            // ================= 12. 匹配器单元（NameMatch） =================
            {
                Eq(NameMatch.Normalize("Ｗｏｒｄ"), "word", "FormKC：全角字母 -> 半角");
                Eq(NameMatch.Normalize("ChromeDriver"), "chromedriver", "归一化保留连写");
                Eq(NameMatch.Normalize("7-Zip File Manager"), "7 zip file manager", "标点 -> 空格");
                Eq(NameMatch.Canonical("7-Zip"), "7zip", "canonical 去空格");
                Eq(NameMatch.Canonical("QQ 音乐"), "qq音乐", "canonical 混合中英");
                Eq(NameMatch.Fold("Ｗｏｒｄ"), "word", "Fold 折叠全角但保留标点");
                Eq(NameMatch.Fold("7-Zip"), "7-zip", "Fold 保留连字符（内置正则依赖）");

                Check(NameMatch.Word("chromedriver", "chrome") == false, "边界：chrome 不命中 chromedriver");
                Check(NameMatch.Word("google chrome", "chrome"), "边界：chrome 命中独立词");
                Check(NameMatch.Word("gitkraken", "git") == false, "边界：git 不命中 gitkraken");
                Check(NameMatch.Word("微信电脑版", "微信"), "边界：中文按子串");
                Check(NameMatch.Word("", "a") == false, "边界：空文本不命中");
                Check(NameMatch.Word("abc", "") == false, "边界：空别名不命中");
                Check(NameMatch.Contains("abc", "b"), "Contains 基本");
                Check(NameMatch.Exact("abc", "abc"), "Exact 相同");
                Check(NameMatch.Exact("abc", "abcd") == false, "Exact 不同长");
                Check(NameMatch.Prefix("abcd", "abc"), "Prefix 命中");
                Check(NameMatch.Prefix("xabcd", "abc") == false, "Prefix 不命中中间");
            }

            Console.WriteLine(failed == 0 ? "ALL PASSED" : failed + " FAILED");
            Environment.Exit(failed == 0 ? 0 : 1);
        }
    }
}
