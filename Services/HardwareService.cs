using System;
using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace Bluetask.Services
{
    public class HardwareService : IDisposable
    {
        private readonly Computer _computer;

        public HardwareService()
        {
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsMotherboardEnabled = true,
                IsStorageEnabled = true,
                IsNetworkEnabled = true
            };
            _computer.Open();
        }

        public IHardware? GetCpu() => _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        public IHardware[] GetGpus() => _computer.Hardware.Where(h => h.HardwareType == HardwareType.GpuAmd || h.HardwareType == HardwareType.GpuNvidia).ToArray();
        public IHardware? GetMemory() => _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Memory);
        public IHardware[] GetStorageDevices() => _computer.Hardware.Where(h => h.HardwareType == HardwareType.Storage).ToArray();
        public IHardware[] GetNetworkAdapters() => _computer.Hardware.Where(h => h.HardwareType == HardwareType.Network).ToArray();
        public IHardware? GetMotherboard() => _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Motherboard);

        public void Update()
        {
            foreach (var hardware in _computer.Hardware)
            {
                hardware.Update();
            }
        }

        public void Dispose()
        {
            _computer.Close();
        }
    }
}


