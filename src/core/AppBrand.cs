namespace Clover
{
    /// <summary>Public brand only. Executable, task name and data paths remain compatible.</summary>
    static class AppBrand
    {
        public const string Name = "归位";
        public const string Product = "归位 · 应用与桌面内容入口";
        public const string Tagline = "应用集中，桌面清爽";
        public const string LegacyName = "四叶草";

        public static string Caption(string section)
        { return string.IsNullOrEmpty(section) ? Name : Name + " · " + section; }

        // Compatibility wording in existing UI; not used for filenames or identity.
        public static string Text(string text)
        { return text == null ? "" : text.Replace(LegacyName, Name); }
    }
}
