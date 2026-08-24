using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using NvAPIWrapper;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.GPU;
using NvAPIWrapper.Native.GPU.Structures;
using NvAPIWrapper.Native.Interfaces.GPU;

namespace Nitrous.Managers
{
    public class NvidiaGpuManager
    {
        private static readonly string AppDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nitrous");
        private static readonly string GpuProfilePath = Path.Combine(AppDataDir, "gpu_oc.json");

        private PhysicalGPU? _internalGpu;
        public bool IsValid => _internalGpu != null;

        public int MaxCoreOffset = 250;
        public int MinCoreOffset = -250;
        public int MaxMemoryOffset = 1000;
        public int MinMemoryOffset = -1000;

        private class GpuOcProfile
        {
            public int Core { get; set; }
            public int Memory { get; set; }
        }

        public NvidiaGpuManager()
        {
            try
            {
                NVIDIA.Initialize();
                _internalGpu = GetInternalDiscreteGpu();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NVAPI] Initialization failed: {ex.Message}");
                _internalGpu = null;
            }
        }

        private static PhysicalGPU? GetInternalDiscreteGpu()
        {
            try
            {
                return PhysicalGPU
                    .GetPhysicalGPUs()
                    .FirstOrDefault(gpu => gpu.SystemType == SystemType.Laptop) 
                    ?? PhysicalGPU.GetPhysicalGPUs().FirstOrDefault();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
                return null;
            }
        }

        public bool GetClocks(out int core, out int memory)
        {
            core = memory = 0;
            if (!IsValid) return false;

            try
            {
                IPerformanceStates20Info states = GPUApi.GetPerformanceStates20(_internalGpu!.Handle);
                core = states.Clocks[PerformanceStateId.P0_3DPerformance][0].FrequencyDeltaInkHz.DeltaValue / 1000;
                memory = states.Clocks[PerformanceStateId.P0_3DPerformance][1].FrequencyDeltaInkHz.DeltaValue / 1000;
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("GET GPU CLOCKS: " + ex.Message);
                return false;
            }
        }

        public int SetClocks(int core, int memory) => SetClocksInternal(core, memory, persist: true);

        private int SetClocksInternal(int core, int memory, bool persist)
        {
            if (!IsValid) return 0;

            if (core < MinCoreOffset || core > MaxCoreOffset) return 0;
            if (memory < MinMemoryOffset || memory > MaxMemoryOffset) return 0;

            GetClocks(out int currentCore, out int currentMemory);

            if (Math.Abs(core - currentCore) < 5 && Math.Abs(memory - currentMemory) < 5) return 0;

            var coreClock = new PerformanceStates20ClockEntryV1(PublicClockDomain.Graphics, new PerformanceStates20ParameterDelta(core * 1000));
            var memoryClock = new PerformanceStates20ClockEntryV1(PublicClockDomain.Memory, new PerformanceStates20ParameterDelta(memory * 1000));

            PerformanceStates20ClockEntryV1[] clocks = { coreClock, memoryClock };
            PerformanceStates20BaseVoltageEntryV1[] voltages = { };

            PerformanceStates20InfoV1.PerformanceState20[] performanceStates = { new PerformanceStates20InfoV1.PerformanceState20(PerformanceStateId.P0_3DPerformance, clocks, voltages) };

            var overclock = new PerformanceStates20InfoV1(performanceStates, 2, 0);

            try
            {
                Debug.WriteLine($"SET GPU CLOCKS: {core}, {memory}");
                GPUApi.SetPerformanceStates20(_internalGpu!.Handle, overclock);
                if (persist) SaveProfile(core, memory);
                return 1;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("SET GPU CLOCKS: " + ex.Message);
                return -1;
            }
        }

        public void ResetOverclock()
        {
            SetClocksInternal(0, 0, persist: false);
            DeleteProfile();
        }
        
        public bool LoadProfile(out int core, out int memory)
        {
            core = memory = 0;
            try
            {
                if (!File.Exists(GpuProfilePath)) return false;
                var profile = JsonSerializer.Deserialize<GpuOcProfile>(File.ReadAllText(GpuProfilePath));
                if (profile == null) return false;
                core = profile.Core;
                memory = profile.Memory;
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NVAPI] LoadProfile failed: {ex.Message}");
                return false;
            }
        }

        private void SaveProfile(int core, int memory)
        {
            try
            {
                Directory.CreateDirectory(AppDataDir);
                var profile = new GpuOcProfile { Core = core, Memory = memory };
                File.WriteAllText(GpuProfilePath, JsonSerializer.Serialize(profile));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NVAPI] SaveProfile failed: {ex.Message}");
            }
        }

        private void DeleteProfile()
        {
            try
            {
                if (File.Exists(GpuProfilePath)) File.Delete(GpuProfilePath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NVAPI] DeleteProfile failed: {ex.Message}");
            }
        }

        public async Task RestoreAtBootAsync()
        {
            if (!LoadProfile(out int core, out int memory)) return;
            if (core == 0 && memory == 0) return;

            const int maxAttempts = 8;
            const int delayMs = 1500;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    if (_internalGpu == null)
                    {
                        try
                        {
                            NVIDIA.Initialize();
                            _internalGpu = GetInternalDiscreteGpu();
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[NVAPI] Boot init attempt {attempt} failed: {ex.Message}");
                        }
                    }

                    if (IsValid)
                    {
                        int result = SetClocksInternal(core, memory, persist: false);
                        if (result == 1)
                        {
                            Debug.WriteLine($"[NVAPI] Boot restore applied on attempt {attempt}: core={core}, memory={memory}");
                            return;
                        }

                        if (GetClocks(out int curCore, out int curMem)
                            && Math.Abs(curCore - core) < 5
                            && Math.Abs(curMem - memory) < 5)
                        {
                            Debug.WriteLine($"[NVAPI] Boot restore already in effect on attempt {attempt}.");
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[NVAPI] Boot restore attempt {attempt} failed: {ex.Message}");
                }

                await Task.Delay(delayMs);
            }

            Debug.WriteLine($"[NVAPI] Boot restore failed after {maxAttempts} attempts.");
        }
    }
}