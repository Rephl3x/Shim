using System;
using System.Runtime.InteropServices;

namespace RigBridge
{
    internal interface IPhysicalForceEffect : IDisposable
    {
        int SetParameters(ref DiEffectDescription description, uint flags);
        int Start(uint iterations, uint flags);
        int Stop();
        int Unload();
    }
    internal interface IPhysicalForceDevice
    {
        int Acquire();
        int Unacquire();
        int GetProperty(IntPtr property, IntPtr value);
        int SetProperty(IntPtr property, IntPtr value);
        int SendForceFeedbackCommand(uint command);
        int CreateEffect(ref Guid type, ref DiEffectDescription description, out IPhysicalForceEffect effect);
    }
    internal sealed class PhysicalForceDevice : IPhysicalForceDevice
    {
        private readonly IDiDevice device;
        internal PhysicalForceDevice(IDiDevice device) { this.device = device; }
        public int Acquire() { return device.Acquire(); }
        public int Unacquire() { return device.Unacquire(); }
        public int GetProperty(IntPtr property, IntPtr value) { return device.GetProperty(property, value); }
        public int SetProperty(IntPtr property, IntPtr value) { return device.SetProperty(property, value); }
        public int SendForceFeedbackCommand(uint command) { return device.SendForceFeedbackCommand(command); }
        public int CreateEffect(ref Guid type, ref DiEffectDescription description, out IPhysicalForceEffect effect)
        {
            IDiEffect native; int result = device.CreateEffect(ref type, ref description, out native, IntPtr.Zero);
            effect = native == null ? null : new PhysicalForceEffect(native); return result;
        }
    }
    internal sealed class PhysicalForceEffect : IPhysicalForceEffect
    {
        private IDiEffect effect;
        internal PhysicalForceEffect(IDiEffect effect) { this.effect = effect; }
        public int SetParameters(ref DiEffectDescription description, uint flags) { return effect.SetParameters(ref description, flags); }
        public int Start(uint iterations, uint flags) { return effect.Start(iterations, flags); }
        public int Stop() { return effect.Stop(); }
        public int Unload() { return effect.Unload(); }
        public void Dispose() { if (effect != null) { Marshal.FinalReleaseComObject(effect); effect = null; } }
    }
}
