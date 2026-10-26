using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace RigBridge
{
    public sealed class BridgeRuntime : IDisposable
    {
        private readonly BridgeProfile profile;
        private readonly IntPtr window;
        private readonly bool monitorOnly;
        private readonly Thread thread;
        private readonly ManualResetEvent stop = new ManualResetEvent(false);
        private readonly object gate = new object();
        private Dictionary<string, InputState> states = new Dictionary<string, InputState>();
        private readonly ConcurrentQueue<FfbPacket> packets = new ConcurrentQueue<FfbPacket>();
        private readonly ConcurrentQueue<string> messages = new ConcurrentQueue<string>();
        private long received;
        private int requestedGain, appliedGain;
        private Exception callbackError;
        public Exception Error { get; private set; }
        public long Frames { get; private set; }
        public long FfbPackets { get { return Interlocked.Read(ref received); } }
        public long WheelStarts { get; private set; }
        public string FfbStatus { get; private set; }
        public bool Running { get { return thread.IsAlive; } }
        public bool Started { get; private set; }
        public bool Stopping { get { return stop.WaitOne(0); } }
        public bool IsMonitorOnly { get { return monitorOnly; } }
        public bool CanChangeGain { get { return !monitorOnly && profile.EnableFfb && Running && !stop.WaitOne(0); } }
        public int AppliedGain { get { return Volatile.Read(ref appliedGain); } }
        public void UpdateGain(int value)
        {
            if (value < 0 || value > 100) throw new ArgumentOutOfRangeException("value");
            if (monitorOnly || !profile.EnableFfb) throw new InvalidOperationException("FFB forwarding must be enabled to change strength live.");
            Interlocked.Exchange(ref requestedGain, value);
        }
        public BridgeRuntime(BridgeProfile profile, IntPtr window, bool monitorOnly)
        {
            this.profile = profile; this.window = window; this.monitorOnly = monitorOnly;
            requestedGain = appliedGain = profile.FfbGain;
            FfbStatus = monitorOnly ? "Read-only monitor." : profile.EnableFfb ? "Waiting for game FFB packets." : "FFB forwarding is off.";
            thread = new Thread(Run) { IsBackground = true, Name = "Shim input and FFB" };
            thread.SetApartmentState(ApartmentState.MTA);
        }
        public void Start() { thread.Start(); }
        public Dictionary<string, InputState> Snapshot() { lock (gate) return new Dictionary<string, InputState>(states); }
        public List<string> TakeMessages()
        { var result = new List<string>(); string message; while (messages.TryDequeue(out message)) result.Add(message); return result; }
        public void RequestStop() { stop.Set(); }
        private void Run()
        {
            var readers = new List<InputReader>(); NativeForceSink force = null; Vjoy output = null; HideDriver hiding = null; HideRecovery recovery = null;
            var seenTypes = new HashSet<int>(); var diagnosticClock = Stopwatch.StartNew();
            try
            {
                using (var direct = new DirectInput())
                {
                    var all = direct.Enumerate();
                    if (!monitorOnly)
                    {
                        profile.Validate(all);
                        if (File.Exists(Store.RecoveryPath)) throw new InvalidOperationException("Restore the previous session's device visibility first.");
                        output = new Vjoy(profile, packet => {
                            Interlocked.Increment(ref received);
                            if (packets.Count >= 2048) Interlocked.CompareExchange(ref callbackError, new InvalidOperationException("FFB queue overflow; bridge stopped."), null);
                            else packets.Enqueue(packet);
                        }, ex => Interlocked.CompareExchange(ref callbackError, ex, null));
                        hiding = new HideDriver();
                        recovery = HideSession.Prepare(hiding, all.Where(d => profile.Devices.Contains(d.Id)).Select(d => d.HidId),
                            HideDriver.ImagePath(System.Windows.Forms.Application.ExecutablePath), profile.EnableFfb ? profile.FfbDevice : null);
                        Store.Write(Store.RecoveryPath, recovery);
                        Process.Start(new ProcessStartInfo {
                            FileName = System.Windows.Forms.Application.ExecutablePath, Arguments = "--watchdog " + recovery.Token,
                            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
                        });
                        // Verify the helper is running before changing any hiding rules.
                        string ready = Store.RecoveryPath + ".ready-" + recovery.Token;
                        var deadline = Stopwatch.StartNew();
                        while (!File.Exists(ready) && deadline.ElapsedMilliseconds < 10000 && !stop.WaitOne(50)) { }
                        if (!File.Exists(ready)) throw new InvalidOperationException("The recovery watchdog could not start. No devices have been hidden.");
                        File.Delete(ready);
                        HideSession.Apply(hiding, recovery);
                    }
                    foreach (var info in all.Where(d => !d.Virtual && profile.Devices.Contains(d.Id)))
                        readers.Add(direct.Open(info, window, !monitorOnly && profile.EnableFfb && info.Id == profile.FfbDevice));
                    if (readers.Count == 0) throw new InvalidOperationException("Select devices to monitor or bridge.");
                    if (!monitorOnly && profile.EnableFfb)
                    {
                        var wheel = readers.First(r => r.Info.Id == profile.FfbDevice);
                        var steering = profile.Bindings.First(b => b.OutputKind == "Axis" && b.OutputIndex == 0);
                        uint objectId = wheel.Info.Controls.First(c => c.Kind == "Axis" && c.Index == steering.InputIndex).ObjectId;
                        force = new NativeForceSink(wheel, profile.FfbGain, profile.InvertFfb, objectId);
                        messages.Enqueue("Physical FFB acquired: " + wheel.Info.Name + ", gain cap " + profile.FfbGain + "%. Restart Forza after any bridge restart so it sends fresh effects.");
                    }
                    var router = force == null ? null : new FfbRouter(force);
                    Started = true;
                    while (!stop.WaitOne(0))
                    {
                        if (callbackError != null) throw callbackError;
                        if (force != null)
                        {
                            int desired = Volatile.Read(ref requestedGain);
                            if (force.StrengthLimit != desired)
                            {
                                force.SetStrength(desired); Volatile.Write(ref appliedGain, desired);
                                messages.Enqueue("FFB strength applied live: " + desired + "%. Effects and device connections kept running.");
                            }
                        }
                        var current = new Dictionary<string, InputState>();
                        foreach (var reader in readers) current[reader.Info.Id] = reader.Read();
                        lock (gate) states = current;
                        if (output != null) output.Write(Mapper.Map(profile.Bindings, current));
                        FfbPacket packet;
                        int count = 0;
                        while (count++ < 512 && packets.TryDequeue(out packet))
                        {
                            if (seenTypes.Add(packet.Type)) messages.Enqueue("First FFB report type " + packet.Type + ": " + BitConverter.ToString(packet.Data));
                            if (router != null) router.Process(packet.Type, packet.Data);
                        }
                        if (force != null)
                        {
                            WheelStarts = force.Starts;
                            FfbStatus = FfbPackets == 0 ? "Waiting for game FFB packets. Launch/restart Forza after the bridge." : force.Status;
                            if (diagnosticClock.ElapsedMilliseconds >= 5000)
                            {
                                messages.Enqueue("FFB: " + FfbPackets + " packets received. " + FfbStatus); diagnosticClock.Restart();
                            }
                        }
                        Frames++;
                        stop.WaitOne(4);
                    }
                }
            }
            catch (Exception ex) { Error = ex; }
            finally
            {
                if (!monitorOnly) messages.Enqueue("Session FFB totals: " + FfbPackets + " packets. " + (force == null ? "Physical forwarding was not active." : force.Status));
                // Keep cleaning up even if a disconnected device rejects one step.
                Cleanup(() => { if (force != null) force.Dispose(); });
                Cleanup(() => { if (output != null) output.Dispose(); });
                foreach (var reader in readers) Cleanup(reader.Dispose);
                if (recovery != null && File.Exists(Store.RecoveryPath)) Cleanup(() => {
                    if (hiding == null) hiding = new HideDriver();
                    HideSession.Restore(hiding, recovery); File.Delete(Store.RecoveryPath);
                });
                if (hiding != null) hiding.Dispose();
            }
        }
        private void Cleanup(Action action) { try { action(); } catch (Exception ex) { Error = Error == null ? ex : new AggregateException(Error, ex); } }
        public void Dispose()
        {
            RequestStop();
            if (thread.IsAlive && !thread.Join(10000)) throw new InvalidOperationException("The bridge is still shutting down. Its recovery watchdog remains active.");
            stop.Dispose();
        }
    }
}
