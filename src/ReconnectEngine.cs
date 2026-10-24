using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace FHMozaFix
{
    public interface IDeviceBackend
    {
        List<DeviceNode> Snapshot();
        Task SetEnabledAsync(Controller controller, bool enabled);
        Task WaitAsync(Controller controller, bool enabled, CancellationToken cancellation);
    }

    public sealed class ReconnectEngine
    {
        private readonly IDeviceBackend backend;
        private readonly IRecoveryStore store;
        private readonly Action<string> log;

        public ReconnectEngine(IDeviceBackend backend, IRecoveryStore store, Action<string> log)
        {
            this.backend = backend;
            this.store = store;
            this.log = log;
        }

        public async Task RunAsync(IList<Controller> order, int settleMilliseconds, int mode,
            Func<int, Controller, Task> pause, CancellationToken cancellation)
        {
            if (store.Exists) throw new InvalidOperationException("Restore the previous session before starting another reconnect.");
            if (order.Count == 0) throw new InvalidOperationException("Select at least one controller.");
            if (order.Select(c => c.TargetId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != order.Count)
                throw new InvalidOperationException("The same device appears more than once.");
            var live = DeviceSafety.Discover(backend.Snapshot());
            var verified = new List<Controller>();
            foreach (var requested in order)
            {
                var current = live.FirstOrDefault(c => DeviceSafety.Eq(c.TargetId, requested.TargetId));
                if (current == null || !current.Allowed)
                    throw new InvalidOperationException(requested.Name + ": " + (current == null ? "Device is no longer available." : current.BlockedReason));
                verified.Add(current); // Use live identities, never profile-supplied metadata.
            }
            cancellation.ThrowIfCancellationRequested();
            // Persist every originally healthy target BEFORE the first disable. Even
            // a command that fails or times out may have already changed the device.
            store.Write(verified.ToList());
            Exception failure = null;
            try
            {
                foreach (var controller in verified.AsEnumerable().Reverse())
                {
                    cancellation.ThrowIfCancellationRequested();
                    var fresh = DeviceSafety.Discover(backend.Snapshot()).FirstOrDefault(c => DeviceSafety.Eq(c.TargetId, controller.TargetId));
                    if (fresh == null || !fresh.Allowed) throw new InvalidOperationException("Device layout changed: " + controller.Name);
                    log("Disconnecting: " + controller.Name);
                    await backend.SetEnabledAsync(controller, false);
                    await backend.WaitAsync(controller, false, cancellation);
                }
                log("All selected controllers are disconnected.");
                await Task.Delay(settleMilliseconds, cancellation);
                for (int index = 0; index < verified.Count; index++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var controller = verified[index];
                    DeviceSafety.ValidateRecovery(controller, backend.Snapshot());
                    log("Connecting " + (index + 1) + "/" + verified.Count + ": " + controller.Name);
                    await backend.SetEnabledAsync(controller, true);
                    await backend.WaitAsync(controller, true, cancellation);
                    await Task.Delay(settleMilliseconds, cancellation);
                    RemovePending(controller.TargetId);
                    if (index < verified.Count - 1 && (mode == 2 || (mode == 1 && index == 0)))
                    {
                        await pause(index, controller);
                        cancellation.ThrowIfCancellationRequested();
                    }
                }
                log("Reconnect complete. Confirm the wheel is Device 1 in Forza and test force feedback.");
            }
            catch (Exception ex) { failure = ex; }
            if (failure != null)
            {
                log("Sequence stopped: " + failure.Message + " Restoring controllers...");
                bool restored = await RestoreAsync(); // Recovery intentionally ignores cancellation.
                if (!restored) throw new InvalidOperationException(failure.Message + " Some devices still need recovery. Use Restore devices.", failure);
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }

        private void RemovePending(string id)
        {
            var pending = store.Read();
            pending.RemoveAll(c => DeviceSafety.Eq(c.TargetId, id));
            store.Write(pending);
        }

        public async Task<bool> RestoreAsync()
        {
            foreach (var controller in store.Read())
            {
                try
                {
                    var nodes = backend.Snapshot();
                    DeviceSafety.ValidateRecovery(controller, nodes);
                    log("Restoring: " + controller.Name);
                    if (!nodes.First(n => DeviceSafety.Eq(n.Id, controller.TargetId)).Healthy)
                        await backend.SetEnabledAsync(controller, true);
                    await backend.WaitAsync(controller, true, CancellationToken.None);
                    RemovePending(controller.TargetId);
                }
                catch (Exception ex) { log("Could not restore " + controller.Name + ": " + ex.Message); }
            }
            bool complete = !store.Exists;
            log(complete ? "All session devices are enabled." : "Recovery is incomplete. The recovery file has been kept; reconnect missing devices to their original USB ports and retry.");
            return complete;
        }
    }
}
