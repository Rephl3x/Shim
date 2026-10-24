using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace RigBridge
{
    public sealed class ForceEffect
    {
        public int Id, Type;
        public uint Duration = UInt32.MaxValue, SamplePeriod, StartDelay, RepeatInterval;
        public int Gain = 10000, Constant, RampStart, RampEnd;
        public int Magnitude, Offset, Phase, Period = 100000;
        public int Center, PositiveCoefficient, NegativeCoefficient, PositiveSaturation = 10000, NegativeSaturation = 10000, Deadband;
        public bool Envelope;
        public int AttackLevel, FadeLevel; public uint AttackTime, FadeTime;
        public double Projection = 1;
    }
    public interface IForceSink : IDisposable
    {
        void Update(ForceEffect effect);
        void Start(int id, uint iterations, bool solo);
        void Free(int id);
        void Stop(int id);
        void Control(int command);
        void Gain(int gain);
    }
    public sealed class FfbRouter
    {
        private readonly IForceSink sink;
        private readonly Dictionary<int, ForceEffect> effects = new Dictionary<int, ForceEffect>();
        public long Packets { get; private set; }
        public FfbRouter(IForceSink sink) { this.sink = sink; }
        public void Process(int type, byte[] data)
        {
            Need(data, 2); Packets++;
            if (type == 12)
            {
                if (data[1] < 1 || data[1] > 6) throw new InvalidOperationException("Unknown FFB device command.");
                sink.Control(data[1]); if (data[1] == 4) effects.Clear(); return;
            }
            if (type == 13) { sink.Gain(data[1] * 10000 / 255); return; }
            if (type >= 16) return; // Feature reports are managed by vJoy's driver.
            int id = data[1];
            if (id < 1 || id > 100) throw new InvalidOperationException("FFB effect ID is out of range.");
            if (type == 11) { sink.Free(id); effects.Remove(id); return; }
            ForceEffect effect;
            if (!effects.TryGetValue(id, out effect)) { effect = new ForceEffect { Id = id }; effects.Add(id, effect); }
            switch (type)
            {
                case 1:
                    Need(data, 16); effect.Type = data[2];
                    if (effect.Type < 1 || effect.Type > 11) throw new NotSupportedException("Custom force data is not supported. Disable Custom Force in Configure vJoy.");
                    effect.Duration = U16(data, 3) == 65535 ? UInt32.MaxValue : Milliseconds((uint)U16(data, 3));
                    effect.RepeatInterval = Milliseconds((uint)U16(data, 5)); effect.SamplePeriod = Milliseconds((uint)U16(data, 7));
                    effect.StartDelay = Milliseconds((uint)U16(data, 9)); effect.Gain = data[11] * 10000 / 255;
                    effect.Projection = 1;
                    // A single-axis steering effect uses signed magnitude and no
                    // direction. Project a two-axis force onto steering when needed.
                    if ((data[13] & 3) == 2) effect.Projection = 0;
                    else if ((data[13] & 3) == 3)
                    {
                        if ((data[13] & 4) != 0) effect.Projection = Math.Sin(U16(data, 14) * 2.0 * Math.PI / 32767);
                        else
                        {
                            Need(data, 18); double x = S16(data, 14), y = S16(data, 16);
                            effect.Projection = x == 0 && y == 0 ? 1 : x / Math.Sqrt(x * x + y * y);
                        }
                    }
                    break;
                case 2:
                    Need(data, 14); effect.Envelope = true;
                    effect.AttackLevel = U16(data, 2); effect.FadeLevel = U16(data, 4);
                    effect.AttackTime = Milliseconds(U32(data, 6)); effect.FadeTime = Milliseconds(U32(data, 10)); break;
                case 3:
                    Need(data, 15); if (data[2] != 0) return; // Y-axis conditions do not act on the wheel motor.
                    effect.Center = S16(data, 3); effect.PositiveCoefficient = S16(data, 5); effect.NegativeCoefficient = S16(data, 7);
                    effect.PositiveSaturation = U16(data, 9); effect.NegativeSaturation = U16(data, 11); effect.Deadband = S16(data, 13); break;
                case 4:
                    Need(data, 12); effect.Magnitude = U16(data, 2); effect.Offset = S16(data, 4);
                    effect.Phase = U16(data, 6); effect.Period = (int)Math.Min(Int32.MaxValue, Milliseconds(U32(data, 8))); break;
                case 5: Need(data, 4); effect.Constant = S16(data, 2); break;
                case 6: Need(data, 6); effect.RampStart = S16(data, 2); effect.RampEnd = S16(data, 4); break;
                case 10:
                    Need(data, 4);
                    if (data[2] == 3) sink.Stop(id);
                    else if (data[2] == 1 || data[2] == 2)
                    {
                        if (effect.Type == 0) throw new InvalidOperationException("FFB start arrived before its effect definition.");
                        sink.Update(effect); sink.Start(id, data[3] == 0 ? UInt32.MaxValue : data[3], data[2] == 2);
                    }
                    else throw new InvalidOperationException("Unknown FFB operation.");
                    return;
                default: throw new NotSupportedException("Unsupported FFB report " + type + ". The bridge has stopped its forces.");
            }
            if (effect.Type != 0) sink.Update(effect);
        }
        public static uint Milliseconds(uint value) { return value > UInt32.MaxValue / 1000 ? UInt32.MaxValue : value * 1000; }
        private static int U16(byte[] data, int index) { return data[index] | data[index + 1] << 8; }
        private static int S16(byte[] data, int index) { return (short)U16(data, index); }
        private static uint U32(byte[] data, int index) { return (uint)(data[index] | data[index + 1] << 8 | data[index + 2] << 16 | data[index + 3] << 24); }
        private static void Need(byte[] data, int length) { if (data == null || data.Length < length) throw new InvalidOperationException("Truncated force-feedback packet."); }
    }
    [StructLayout(LayoutKind.Sequential)] internal struct DiEnvelope
    { public uint Size, AttackLevel, AttackTime, FadeLevel, FadeTime; }

    public sealed class NativeForceSink : IForceSink
    {
        private readonly InputReader wheel;
        private readonly Dictionary<int, IDiEffect> effects = new Dictionary<int, IDiEffect>();
        private readonly Dictionary<int, int> types = new Dictionary<int, int>();
        private readonly int limit;
        private readonly bool invert;
        private readonly uint axis;
        private int deviceGain = 10000;
        private int previousAutocenter = -1, previousGain = -1;
        public NativeForceSink(InputReader wheel, int limit, bool invert, uint axis)
        {
            this.wheel = wheel; this.limit = limit; this.invert = invert; this.axis = axis;
            wheel.Device.Unacquire();
            previousAutocenter = ReadDword(9); previousGain = ReadDword(7);
            try
            {
                SetDword(9, 0);
                DirectInput.Check(wheel.Device.Acquire(), "Acquire wheel for FFB");
                DirectInput.Check(wheel.Device.SendForceFeedbackCommand(1), "Reset wheel effects");
                Gain(10000);
            }
            catch
            {
                wheel.Device.Unacquire();
                if (previousGain >= 0) SetDword(7, previousGain);
                if (previousAutocenter >= 0) SetDword(9, previousAutocenter);
                throw;
            }
        }
        private int ReadDword(int property)
        {
            IntPtr memory = Marshal.AllocHGlobal(20);
            try { Marshal.Copy(new[] { 20, 16, 0, 0, 0 }, 0, memory, 5); return wheel.Device.GetProperty(new IntPtr(property), memory) < 0 ? -1 : Marshal.ReadInt32(memory, 16); }
            finally { Marshal.FreeHGlobal(memory); }
        }
        private void SetDword(int property, int value)
        {
            IntPtr memory = Marshal.AllocHGlobal(20);
            try { Marshal.Copy(new[] { 20, 16, 0, 0, value }, 0, memory, 5); DirectInput.Check(wheel.Device.SetProperty(new IntPtr(property), memory), "Set wheel FFB property"); }
            finally { Marshal.FreeHGlobal(memory); }
        }
        public void Gain(int gain) { deviceGain = Math.Max(0, Math.Min(10000, gain)); SetDword(7, deviceGain * limit / 100); }
        public void Update(ForceEffect source)
        {
            int direction = invert ? -1 : 1;
            double projection = Math.Max(-1, Math.Min(1, source.Projection));
            int type = source.Type;
            if (projection * direction < 0 && type == 6) type = 7;
            else if (projection * direction < 0 && type == 7) type = 6;
            int[] parameters;
            if (type == 1) parameters = new[] { Signed(source.Constant * projection * direction) };
            else if (type == 2) parameters = new[] { Signed(source.RampStart * projection * direction), Signed(source.RampEnd * projection * direction) };
            else if (type <= 7) parameters = new[] {
                (int)Math.Min(10000, Math.Abs(source.Magnitude * projection)), Signed(source.Offset * projection * direction),
                (source.Phase + (projection * direction < 0 && type <= 5 ? 18000 : 0)) % 36000, Math.Max(1, source.Period) };
            else parameters = new[] { Signed(source.Center), Signed(source.PositiveCoefficient), Signed(source.NegativeCoefficient),
                Math.Max(0, Math.Min(10000, source.PositiveSaturation)), Math.Max(0, Math.Min(10000, source.NegativeSaturation)), Math.Max(0, Math.Min(10000, source.Deadband)) };
            IntPtr data = Marshal.AllocHGlobal(parameters.Length * 4), axes = Marshal.AllocHGlobal(4), directions = Marshal.AllocHGlobal(4), envelope = IntPtr.Zero;
            try
            {
                Marshal.Copy(parameters, 0, data, parameters.Length); Marshal.WriteInt32(axes, (int)axis); Marshal.WriteInt32(directions, 0);
                if (source.Envelope)
                {
                    envelope = Marshal.AllocHGlobal(20);
                    Marshal.StructureToPtr(new DiEnvelope { Size = 20, AttackLevel = (uint)Math.Max(0, Math.Min(10000, source.AttackLevel)), FadeLevel = (uint)Math.Max(0, Math.Min(10000, source.FadeLevel)), AttackTime = source.AttackTime, FadeTime = source.FadeTime }, envelope, false);
                }
                var description = new DiEffectDescription {
                    Size = (uint)Marshal.SizeOf(typeof(DiEffectDescription)), Flags = 0x11,
                    Duration = source.Duration, SamplePeriod = source.SamplePeriod, Gain = (uint)Math.Max(0, Math.Min(10000, source.Gain)),
                    TriggerButton = UInt32.MaxValue, RepeatInterval = source.RepeatInterval, AxesCount = 1,
                    Axes = axes, Direction = directions, Envelope = envelope, TypeSize = (uint)(parameters.Length * 4), TypeData = data, StartDelay = source.StartDelay
                };
                IDiEffect effect;
                if (effects.TryGetValue(source.Id, out effect) && types[source.Id] != type) { Free(source.Id); effect = null; }
                if (effect == null)
                {
                    Guid guid = new Guid((0x13541c20 + type - 1).ToString("x8") + "-8e33-11d0-9ad0-00a0c9a06e35");
                    DirectInput.Check(wheel.Device.CreateEffect(ref guid, ref description, out effect, IntPtr.Zero), "Create physical FFB effect type " + type);
                    effects[source.Id] = effect; types[source.Id] = type;
                }
                else DirectInput.Check(effect.SetParameters(ref description, 0x400003ff), "Update physical FFB effect");
            }
            finally { Marshal.FreeHGlobal(data); Marshal.FreeHGlobal(axes); Marshal.FreeHGlobal(directions); if (envelope != IntPtr.Zero) Marshal.FreeHGlobal(envelope); }
        }
        private static int Signed(double value) { return (int)Math.Max(-10000, Math.Min(10000, value)); }
        public void Start(int id, uint iterations, bool solo)
        { IDiEffect effect; if (!effects.TryGetValue(id, out effect)) throw new InvalidOperationException("Cannot start an undefined FFB effect."); DirectInput.Check(effect.Start(iterations, solo ? 1u : 0u), "Start wheel effect"); }
        public void Stop(int id) { IDiEffect effect; if (effects.TryGetValue(id, out effect)) DirectInput.Check(effect.Stop(), "Stop wheel effect"); }
        public void Free(int id)
        {
            IDiEffect effect;
            if (effects.TryGetValue(id, out effect)) { effect.Stop(); effect.Unload(); Marshal.FinalReleaseComObject(effect); effects.Remove(id); types.Remove(id); }
        }
        public void Control(int command)
        {
            uint[] commands = { 0, 16, 32, 2, 1, 4, 8 };
            DirectInput.Check(wheel.Device.SendForceFeedbackCommand(commands[command]), "Send wheel FFB control");
            if (command == 4) { foreach (int id in new List<int>(effects.Keys)) Free(id); Gain(deviceGain); }
        }
        public void Dispose()
        {
            try { wheel.Device.SendForceFeedbackCommand(2); wheel.Device.SendForceFeedbackCommand(32); }
            finally
            {
                foreach (int id in new List<int>(effects.Keys)) Free(id);
                wheel.Device.Unacquire();
                if (previousGain >= 0) SetDword(7, previousGain);
                if (previousAutocenter >= 0) SetDword(9, previousAutocenter);
            }
        }
    }
}
