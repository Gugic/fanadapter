using System;
using System.Runtime.CompilerServices;
using System.Windows;
using Fanadapter.Core;
using SimHub.Plugins;
using SimHub.Plugins.UI.Axis;

namespace Fanadapter.SimHub
{
    /// <summary>Shared source lookup for the pedal stream and its live preview.</summary>
    public static class AxisSourceReader
    {
        private sealed class AssignmentHolder
        {
            public volatile AxisAssignment Value;
        }

        // Native assignments subscribe to SimHub's axis registration events,
        // including roles registered after plugin Init. Keep one per settings
        // object, shared by the picker and stream, without retaining old plugins.
        private static readonly ConditionalWeakTable<AxisSource, AssignmentHolder> Assignments =
            new ConditionalWeakTable<AxisSource, AssignmentHolder>();

        public static void Initialize(DriveSettings settings)
        {
            foreach (var source in settings.AllAxes())
                if (source != null) GetAssignment(source);
        }

        public static AxisAssignment GetAssignment(AxisSource source) =>
            Assignments.GetValue(source, CreateAssignment).Value;

        public static void SetAssignment(AxisSource source, AxisAssignment assignment) =>
            Assignments.GetValue(source, CreateAssignment).Value = assignment;

        private static AssignmentHolder CreateAssignment(AxisSource source)
        {
            // WeakEventManager owns dispatcher-bound subscriptions. Build these
            // on the application dispatcher before starting the worker stream.
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
                return dispatcher.Invoke(() => CreateAssignment(source));

            Enum.TryParse(source.AxisMovement, out AxisMovement movement);
            return new AssignmentHolder
            {
                Value = new AxisAssignment { AxisName = source.AxisName, AxisMovement = movement },
            };
        }

        public static object Read(PluginManager manager, AxisSource source)
        {
            if (manager == null || source == null || !source.IsConfigured) return null;
            var assignment = GetAssignment(source);
            if (!Enum.IsDefined(typeof(AxisMovement), assignment.AxisMovement) || assignment.AxisMovement == AxisMovement.None)
                return null;

            // SimHub's public assignment API uses the latest sampled value and
            // applies the picker's direction. It returns null until initialized
            // or while unavailable. A missing role never falls back to a device.
            return assignment.GetValue();
        }
    }
}
