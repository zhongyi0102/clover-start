using System;

namespace Clover
{
    /// <summary>Eligibility only: page rendering and controls never belong in this policy.</summary>
    static class CommonUsagePolicy
    {
        public static bool IsEligible(AppItem app, long dismissedAt, long nowTicks)
        {
            if (app == null || app.Hidden || app.ExcludedEntry || app.Role != AppRole.User || app.LowValue) return false;
            if (app.Usage <= 0 || string.IsNullOrEmpty(app.Path)) return false;
            if (dismissedAt > 0 && app.LastUsed <= dismissedAt) return false;
            return app.LastUsed > nowTicks - UsageStore.RecentWindowTicks && app.LastUsed <= nowTicks;
        }
    }
}
