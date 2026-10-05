using System;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Fanadapter.SimHub.Tests
{
    internal static class SimHubTestEnvironment
    {
        static SimHubTestEnvironment()
        {
            var simHubDir = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SimHubDir.txt")).Trim();
            AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
            {
                var path = Path.Combine(simHubDir, new AssemblyName(args.Name).Name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
        }

        public static void PumpUntil(Task work)
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            do
            {
                var frame = new DispatcherFrame();
                dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                if (DateTime.UtcNow > deadline) throw new TimeoutException("WPF test did not complete");
                Thread.Yield();
            } while (!work.IsCompleted);
            work.GetAwaiter().GetResult();
            var drain = new DispatcherFrame();
            dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => drain.Continue = false));
            Dispatcher.PushFrame(drain);
        }

        public static void OnSta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { failure = ex; }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "STA test timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        public static void Set(object target, string field, object value) =>
            target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
    }
}
