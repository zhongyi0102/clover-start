// 占位文件：build.ps1 每次编译时会覆盖它，写入真实的版本号与构建时间。
// 提交这个占位版本是为了让 clone 下来的人不跑 build.ps1 也能直接编译。
namespace Clover
{
    static class BuildInfo
    {
        public const string Version = "dev";
        public const string Stamp = "dev";
        public const string BuildTime = "unknown";
        public static string Describe { get { return "四叶草 v" + Version + "（" + BuildTime + " 构建）"; } }
    }
}