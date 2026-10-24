using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace RigBridge.Tests
{
    internal static class PhysicalForceTests
    {
        public static void Run(Action<string, Action> test)
        {
            test("MOZA updates exclude immutable axes and change only magnitude", () => {
                var device = new FakeDevice();
                using (var sink = new NativeForceSink(device, 25, false, 0x01000002))
                {
                    var source = Constant(); sink.Update(source); source.Constant = -1234; sink.Update(source);
                    var effect = device.Effects.Single();
                    Assert(effect.UpdateFlags.SequenceEqual(new[] { 0x40000100u }), "Full parameters/axes were resent");
                    Assert(effect.Parameters[0] == -1234 && sink.Created == 1 && sink.Updated == 1, "Magnitude did not reach the driver");
                    Assert(effect.Starts == 0, "Updating an effect started playback");
                    Assert(device.Axis == 0x01000002 && device.Direction == 10000 && device.Flags == 0x11, "Axis or Cartesian direction incorrect");
                }
            });
            test("Unchanged updates are skipped before explicit game start", () => {
                var device = new FakeDevice();
                using (var sink = new NativeForceSink(device, 25, false, 2))
                {
                    var source = Constant(); sink.Update(source); sink.Update(source); sink.Update(source);
                    Assert(sink.Updated == 0 && device.Effects.Single().UpdateFlags.Count == 0, "Duplicate update reached hardware");
                    sink.Start(1, 3, false); Assert(sink.Starts == 1 && device.Effects.Single().Iterations == 3, "Explicit start was lost");
                }
            });
            test("Effect timing and gain changes omit axes direction and trigger flags", () => {
                var device = new FakeDevice();
                using (var sink = new NativeForceSink(device, 25, false, 2))
                {
                    var source = Constant(); sink.Update(source); source.Duration = 500000; source.SamplePeriod = 1000; source.Gain = 2000; source.RepeatInterval = 3000; source.StartDelay = 4000;
                    sink.Update(source);
                    uint flags = device.Effects.Single().UpdateFlags.Single();
                    Assert(flags == (0x40000000u | 1 | 2 | 4 | 16 | 512), "Incorrect changed fields");
                    Assert((flags & (0x20u | 0x40u | 8u)) == 0, "Immutable/unmodified fields were sent");
                }
            });
            test("A playing-effect restriction retries once with DirectInput playback preservation", () => {
                var device = new FakeDevice();
                using (var sink = new NativeForceSink(device, 25, false, 2))
                {
                    var source = Constant(); sink.Update(source); sink.Start(1, 1, false);
                    device.Effects[0].RejectNextDynamic = true; source.Constant = 3000; sink.Update(source);
                    Assert(device.Effects[0].UpdateFlags.SequenceEqual(new[] { 0x40000100u, 0x100u }), "No bounded fallback");
                    Assert(sink.RestartedUpdates == 1 && sink.Starts == 1, "Fallback invented a start or was not recorded");
                }
            });
            test("Other driver failures are reported and never silently bypassed", () => {
                var device = new FakeDevice();
                using (var sink = new NativeForceSink(device, 25, false, 2))
                {
                    var source = Constant(); sink.Update(source); device.Effects[0].NextError = unchecked((int)0x80070005); source.Constant = 4000;
                    bool threw = false; try { sink.Update(source); } catch (InvalidOperationException error) { threw = error.Message.Contains("80070005"); }
                    Assert(threw && sink.Updated == 0 && device.Effects[0].UpdateFlags.Count == 1, "Driver failure ignored or retried as unrelated error");
                }
            });
            test("Device gain cap and actuator mute/reset commands are preserved", () => {
                var device = new FakeDevice();
                using (var sink = new NativeForceSink(device, 25, false, 2))
                {
                    Assert(device.Properties[7] == 2500 && sink.ActuatorsEnabled && device.Commands.Contains(16), "Startup gain/actuators incorrect");
                    sink.Gain(2000); Assert(device.Properties[7] == 500 && sink.EffectiveGain == 500, "Gain cap ignored");
                    sink.Control(2); sink.Control(4);
                    Assert(!sink.ActuatorsEnabled && device.Commands.Last() == 32 && device.Properties[7] == 500, "Reset unmuted a game-muted wheel");
                    sink.Control(1); sink.Control(5); Assert(sink.ActuatorsEnabled && sink.Paused, "Enable/pause lost");
                    sink.Control(6); Assert(!sink.Paused, "Continue lost");
                }
                Assert(device.Properties[7] == 8765 && device.Properties[9] == 1 && device.Commands.Contains(2) && device.Commands.Contains(32), "Original settings not restored on stop");
            });
            test("Changing effect type releases the old native object", () => {
                var device = new FakeDevice();
                using (var sink = new NativeForceSink(device, 25, false, 2))
                {
                    var source = Constant(); sink.Update(source); source.Type = 8; sink.Update(source);
                    Assert(device.Effects.Count == 2 && device.Effects[0].Disposed && sink.Created == 2, "Old native type was reused");
                    source.PositiveCoefficient = 1000; sink.Update(source); Assert(sink.Updated == 1, "New type update snapshot missing");
                }
            });
            test("Envelope addition and removal send only the envelope field", () => {
                var device = new FakeDevice();
                using (var sink = new NativeForceSink(device, 25, false, 2))
                {
                    var source = Constant(); sink.Update(source); source.Envelope = true; source.AttackLevel = 500; source.AttackTime = 20000; sink.Update(source);
                    Assert(device.Effects[0].EnvelopePresent, "Envelope not sent");
                    source.Envelope = false; sink.Update(source);
                    Assert(!device.Effects[0].EnvelopePresent && device.Effects[0].UpdateFlags.All(f => f == 0x40000080u), "Envelope not detached correctly");
                }
            });
            test("A Y-only condition cannot apply spring or damper force to steering", () => {
                var device = new FakeDevice();
                using (var sink = new NativeForceSink(device, 25, false, 2))
                {
                    sink.Update(new ForceEffect { Id = 1, Type = 8, Projection = 0, PositiveCoefficient = 8000, NegativeCoefficient = -8000 });
                    Assert(device.Effects[0].Parameters.Skip(1).Take(4).All(p => p == 0), "Ignored Y condition reached wheel motor");
                }
            });
            test("Packet decoding reaches the native updater and starts only on the operation report", () => {
                var device = new FakeDevice();
                using (var sink = new NativeForceSink(device, 25, false, 2))
                {
                    var router = new FfbRouter(sink); var definition = new byte[18]; definition[0] = 0x11; definition[1] = 1; definition[2] = 1; definition[3] = definition[4] = 255; definition[11] = 255; definition[13] = 1;
                    router.Process(1, definition); router.Process(5, new byte[] { 0x15, 1, 0xd0, 7 });
                    Assert(sink.Updated == 1 && sink.Starts == 0 && device.Effects[0].Parameters[0] == 2000, "Definition/magnitude path failed");
                    router.Process(10, new byte[] { 0x1a, 1, 1, 1 }); Assert(sink.Starts == 1 && sink.Updated == 1, "Start resent all parameters or failed");
                }
            });
            test("Already initialized errors do not accuse another app of owning the wheel", () => {
                string message = DirectInput.FailureMessage(unchecked((int)0x800704df), "Update effect");
                Assert(message.Contains("DIERR_ALREADYINITIALIZED") && !message.Contains("Close other apps"), "Misleading ownership advice");
            });
        }
        private static ForceEffect Constant() { return new ForceEffect { Id = 1, Type = 1, Constant = 1000 }; }
        private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        private sealed class FakeDevice : IPhysicalForceDevice
        {
            public readonly Dictionary<int, int> Properties = new Dictionary<int, int> { { 7, 8765 }, { 9, 1 } };
            public readonly List<FakeEffect> Effects = new List<FakeEffect>(); public readonly List<uint> Commands = new List<uint>();
            public uint Axis, Flags; public int Direction;
            public int Acquire() { return 0; } public int Unacquire() { return 0; }
            public int GetProperty(IntPtr property, IntPtr value) { Marshal.WriteInt32(value, 16, Properties[property.ToInt32()]); return 0; }
            public int SetProperty(IntPtr property, IntPtr value) { Properties[property.ToInt32()] = Marshal.ReadInt32(value, 16); return 0; }
            public int SendForceFeedbackCommand(uint command) { Commands.Add(command); return 0; }
            public int CreateEffect(ref Guid type, ref DiEffectDescription description, out IPhysicalForceEffect effect)
            {
                Axis = unchecked((uint)Marshal.ReadInt32(description.Axes)); Direction = Marshal.ReadInt32(description.Direction); Flags = description.Flags;
                var created = new FakeEffect(); created.Capture(description); Effects.Add(created); effect = created; return 0;
            }
        }
        private sealed class FakeEffect : IPhysicalForceEffect
        {
            public readonly List<uint> UpdateFlags = new List<uint>(); public int[] Parameters; public bool EnvelopePresent, Disposed, RejectNextDynamic; public long Starts; public uint Iterations; public int NextError;
            public void Capture(DiEffectDescription description) { Parameters = new int[description.TypeSize / 4]; Marshal.Copy(description.TypeData, Parameters, 0, Parameters.Length); EnvelopePresent = description.Envelope != IntPtr.Zero; }
            public int SetParameters(ref DiEffectDescription description, uint flags)
            {
                UpdateFlags.Add(flags);
                if ((flags & 0x20) != 0) return unchecked((int)0x800704df);
                if (RejectNextDynamic) { RejectNextDynamic = false; return unchecked((int)0x80040208); }
                if (NextError != 0) return NextError;
                Capture(description); return 0;
            }
            public int Start(uint iterations, uint flags) { Starts++; Iterations = iterations; return 0; }
            public int Stop() { return 0; } public int Unload() { return 0; }
            public void Dispose() { Disposed = true; }
        }
    }
}
