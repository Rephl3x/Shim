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
        private Exception callbackError;
        public Exception Error { get; private set; }
        public long Frames { get; private set; }
        public long FfbPackets { get; private set; }
        public bool Running { get { return thread.IsAlive; } }
        public bool Started { get; private set; }
        public BridgeRuntime(BridgeProfile profile, IntPtr window, bool monitorOnly)
        {
            this.profile = profile; this.window = window; this.monitorOnly = monitorOnly;
            thread = new Thread(Run) { IsBackground = true, Name = "FH Rig Bridge input and FFB" };
            thread.SetApartmentState(ApartmentState.MTA);
        }
        public void Start() { thread.Start(); }
        public Dictionary<string, InputState> Snapshot() { lock (gate) return new Dictionary<string, InputState>(states); }
        public void RequestStop() { stop.Set(); }
        private void Run()
        {
            var readers = new List<InputReader>(); IForceSink force = null; Vjoy output = null; HideDriver hiding = null; HideRecovery recovery = null;
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
                    }
                    var router = force == null ? null : new FfbRouter(force);
                    Started = true;
                    while (!stop.WaitOne(0))
                    {
                        if (callbackError != null) throw callbackError;
                        var current = new Dictionary<string, InputState>();
                        foreach (var reader in readers) current[reader.Info.Id] = reader.Read();
                        lock (gate) states = current;
                        if (output != null) output.Write(Mapper.Map(profile.Bindings, current));
                        FfbPacket packet;
                        int count = 0;
                        while (count++ < 512 && packets.TryDequeue(out packet)) { if (router != null) router.Process(packet.Type, packet.Data); }
                        if (router != null) FfbPackets = router.Packets;
                        Frames++;
                        stop.WaitOne(4);
                    }
                }
            }
            catch (Exception ex) { Error = ex; }
            finally
            {
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
