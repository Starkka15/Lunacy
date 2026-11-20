using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.Background;
using Windows.ApplicationModel.ExtendedExecution;

namespace LunaKit
{
    /// Running in the background, as webOS's Synergy services did with the screen off and other
    /// apps in front (First Use's "Running in the Background"; AndroidLuna's battery
    /// optimisation exemption and its wake alarms, Power.kt).
    ///
    /// Windows 10 Mobile suspends an app that leaves the front. Two things keep Lunacy's
    /// services going:
    /// - Extended execution: asked for at start, it keeps the process running after it leaves
    ///   the front until Windows needs the memory or the battery saver says otherwise.
    /// - In-process background tasks: a timer every 15 minutes (Windows' shortest) and a
    ///   network change. Each wakes the activity manager and com.palm.power's timeouts
    ///   (LunaShell.BackgroundWake) and holds the process up while they run.
    /// Both need the user's leave: BackgroundExecutionManager's access, which First Use asks
    /// for; Windows' own switch is Settings > Battery > Battery usage by app.
    ///
    /// A task that fires after Windows has ended Lunacy starts a process with no window, and the
    /// webOS services run in the shell's WebView: that wake only notes itself in the log, and
    /// the next start catches up (the activity manager runs whatever came due).
    public static class Background
    {
        private const string TimerTask = "lunacy-timer";
        private const string NetTask = "lunacy-network";
        /// How long a wake keeps the task open, at most: Windows ends an in-process task at 30 s.
        private const int MaxHoldMs = 25000;

        internal static LunaShell Shell;
        private static ExtendedExecutionSession session;
        private static BackgroundTaskDeferral deferral;
        private static DateTime holdUntil;
        private static readonly object gate = new object();

        internal static bool Granted
        {
            get
            {
                var s = BackgroundExecutionManager.GetAccessStatus();
#pragma warning disable 618
                return s == BackgroundAccessStatus.AlwaysAllowed || s == BackgroundAccessStatus.AllowedSubjectToSystemPolicy
                    || s == BackgroundAccessStatus.AllowedWithAlwaysOnRealTimeConnectivity || s == BackgroundAccessStatus.AllowedMayUseActiveRealTimeConnectivity;
#pragma warning restore 618
            }
        }

        /// Asks Windows. True when allowed; the tasks are registered then.
        internal static async Task<bool> Request()
        {
            BackgroundAccessStatus s;
            try { s = await BackgroundExecutionManager.RequestAccessAsync(); }
            catch (Exception e) { LunaLog.Write("[background] access request failed: " + e.Message); return false; }
            LunaLog.Write("[background] access: " + s);
            if (!Granted) return false;
            Register();
            await Extend();
            return true;
        }

        /// At start: the tasks and extended execution, if the user has allowed them.
        internal static async Task Start(LunaShell shell)
        {
            Shell = shell;
            if (!Granted) { LunaLog.Write("[background] not allowed: " + BackgroundExecutionManager.GetAccessStatus()); return; }
            Register();
            await Extend();
        }

        private static void Register()
        {
            try
            {
                var have = BackgroundTaskRegistration.AllTasks.Values.Select(t => t.Name).ToList();
                if (!have.Contains(TimerTask))
                {
                    var b = new BackgroundTaskBuilder { Name = TimerTask };
                    b.SetTrigger(new TimeTrigger(15, false));
                    b.Register();
                }
                if (!have.Contains(NetTask))
                {
                    var b = new BackgroundTaskBuilder { Name = NetTask };
                    b.SetTrigger(new SystemTrigger(SystemTriggerType.NetworkStateChange, false));
                    b.Register();
                }
            }
            catch (Exception e) { LunaLog.Write("[background] can't register tasks: " + e.Message); }
        }

        /// Extended execution, again whenever Windows has taken it back and Lunacy is in front.
        internal static async Task Extend()
        {
            if (session != null) return;
            var s = new ExtendedExecutionSession { Reason = ExtendedExecutionReason.Unspecified, Description = "webOS services: mail, messages, reminders" };
            s.Revoked += (o, e) =>
            {
                LunaLog.Write("[background] extended execution revoked: " + e.Reason);
                lock (gate) { if (session == s) session = null; }
                s.Dispose();
            };
            ExtendedExecutionResult r;
            try { r = await s.RequestExtensionAsync(); }
            catch (Exception e) { LunaLog.Write("[background] extended execution failed: " + e.Message); s.Dispose(); return; }
            LunaLog.Write("[background] extended execution: " + r);
            if (r == ExtendedExecutionResult.Allowed) lock (gate) session = s;
            else s.Dispose();
        }

        /// The process's memory against Windows' limit for it, for the log: a phone gives an app
        /// a fixed share, and the WebView stops drawing when Lunacy reaches it.
        internal static string Memory()
        {
            try
            {
                return "memory " + (Windows.System.MemoryManager.AppMemoryUsage >> 20) + " MB of " + (Windows.System.MemoryManager.AppMemoryUsageLimit >> 20) + " MB ("
                    + Windows.System.MemoryManager.AppMemoryUsageLevel + ")";
            }
            catch (Exception e) { return "memory unknown: " + e.Message; }
        }

        /// App.OnBackgroundActivated: a timer or network wake.
        public static void Activated(IBackgroundTaskInstance task)
        {
            var d = task.GetDeferral();
            task.Canceled += (s, r) => { LunaLog.Write("[background] task cancelled: " + r); Release(d); };
            var shell = Shell;
            LunaLog.Write("[background] wake: " + task.Task.Name + (shell == null ? " (no shell: caught up at the next start)" : "") + ", " + Memory());
            if (shell == null) { d.Complete(); return; }
            lock (gate) { deferral = d; holdUntil = DateTime.UtcNow.AddSeconds(5); }
            shell.BackgroundWake();
            Task.Run(async () =>
            {
                while (true)
                {
                    DateTime until;
                    lock (gate) { if (deferral != d) return; until = holdUntil; }
                    var left = until - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero) break;
                    await Task.Delay(left);
                }
                Release(d);
            });
        }

        /// Keeps a wake going `ms` longer (a timeout's activity_duration_ms, an activity starting).
        internal static void Hold(long ms)
        {
            lock (gate)
            {
                if (deferral == null) return;
                var until = DateTime.UtcNow.AddMilliseconds(Math.Min(ms, MaxHoldMs));
                if (until > holdUntil) holdUntil = until;
            }
        }

        private static void Release(BackgroundTaskDeferral d)
        {
            lock (gate) { if (deferral == d) deferral = null; }
            try { d.Complete(); } catch (Exception) { }
        }
    }
}
