using System.Reflection;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace FUPlayer.Core.Dsp.Acceleration;

/// <summary>Headless Vulkan compute with device-local filter storage and explicit transfer staging.</summary>
internal sealed unsafe class VulkanComputeApi : ComputeApi
{
    private sealed class Runtime
    {
        public readonly Vk Vk = Vk.GetApi();
        public readonly Instance Instance;
        public Runtime()
        {
            ApplicationInfo application = new() { SType = StructureType.ApplicationInfo, ApiVersion = Vk.Version11 };
            InstanceCreateInfo create = new() { SType = StructureType.InstanceCreateInfo, PApplicationInfo = &application };
            Check(Vk.CreateInstance(in create, null, out Instance), "create instance");
        }
    }
    private static readonly Lazy<Runtime> Shared = new(() => new Runtime());
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly PhysicalDevice _physical;
    private readonly Queue _queue;
    private readonly uint _family;
    private readonly bool _double;
    private readonly object _submitLock = new();
    private DescriptorSetLayout _descriptors;
    private PipelineLayout _layout;
    private PhysicalDeviceMemoryProperties _memory;

    private sealed class Allocation(Buffer buffer, DeviceMemory memory, ulong size)
    {
        public readonly Buffer Buffer = buffer;
        public readonly DeviceMemory Memory = memory;
        public readonly ulong Size = size;
    }
    private sealed class Commands(CommandPool pool, CommandBuffer buffer, DescriptorPool descriptors, Fence fence)
    {
        public readonly CommandPool Pool = pool;
        public readonly CommandBuffer Buffer = buffer;
        public readonly DescriptorPool Descriptors = descriptors;
        public readonly Fence Fence = fence;
        public Allocation? Staging;
        public bool Recording;
        public bool Pending;
    }
    private sealed class Kernel(string name, ShaderModule module)
    {
        public readonly string Name = name;
        public readonly ShaderModule Module = module;
        public readonly ulong[] Arguments = new ulong[16];
        public readonly Dictionary<uint, Pipeline> Pipelines = new();
    }

    private static void Check(Result result, string operation)
    {
        if (result != Result.Success) throw new OpenClException($"Vulkan {operation}: {result}");
    }
    private static T Get<T>(nint handle) where T : class => (T)GCHandle.FromIntPtr(handle).Target!;
    private static nint Hold(object value) => GCHandle.ToIntPtr(GCHandle.Alloc(value));

    internal static IReadOnlyList<GpuDevice> Discover()
    {
        Runtime runtime = Shared.Value;
        Vk vk = runtime.Vk;
        uint count = 0;
        Check(vk.EnumeratePhysicalDevices(runtime.Instance, &count, null), "device count");
        var handles = new PhysicalDevice[count];
        fixed (PhysicalDevice* p = handles)
            Check(vk.EnumeratePhysicalDevices(runtime.Instance, &count, p), "devices");
        var devices = new List<GpuDevice>();
        foreach (PhysicalDevice physical in handles)
        {
            vk.GetPhysicalDeviceProperties(physical, out PhysicalDeviceProperties properties);
            if (properties.DeviceType is not (PhysicalDeviceType.DiscreteGpu or PhysicalDeviceType.IntegratedGpu)) continue;
            if (properties.ApiVersion < Vk.Version11 || properties.Limits.MaxPerStageDescriptorStorageBuffers < 6) continue;
            vk.GetPhysicalDeviceFeatures(physical, out PhysicalDeviceFeatures features);
            PhysicalDeviceIDProperties identity = new() { SType = StructureType.PhysicalDeviceIDProperties };
            PhysicalDeviceProperties2 extended = new() { SType = StructureType.PhysicalDeviceProperties2, PNext = &identity };
            vk.GetPhysicalDeviceProperties2(physical, &extended);
            string uuid = Convert.ToHexString(new ReadOnlySpan<byte>(identity.DeviceUuid, 16));
            vk.GetPhysicalDeviceMemoryProperties(physical, out PhysicalDeviceMemoryProperties memory);
            long bytes = 0;
            for (int i = 0; i < memory.MemoryHeapCount; i++)
                if ((memory.MemoryHeaps[i].Flags & MemoryHeapFlags.DeviceLocalBit) != 0)
                    bytes += (long)memory.MemoryHeaps[i].Size;
            string name = Marshal.PtrToStringUTF8((nint)properties.DeviceName)!;
            string vendor = properties.VendorID switch { 0x10de => "NVIDIA", 0x1002 => "AMD", 0x8086 => "Intel", _ => $"0x{properties.VendorID:x4}" };
            devices.Add(new GpuDevice
            {
                Id = $"vulkan:{properties.VendorID:x4}:{properties.DeviceID:x4}:{uuid}:{name}",
                Name = name, Vendor = vendor, Platform = "Vulkan", Backend = GpuBackend.Vulkan,
                SupportsDouble = features.ShaderFloat64, IsGpu = true,
                IsDiscrete = properties.DeviceType == PhysicalDeviceType.DiscreteGpu,
                GlobalMemoryBytes = bytes, MaxAllocationBytes = Math.Min(bytes * 3 / 4, properties.Limits.MaxStorageBufferRange),
                Handle = physical.Handle, MaxGroupSize = (int)Math.Min(properties.Limits.MaxComputeWorkGroupInvocations, properties.Limits.MaxComputeWorkGroupSize[0]),
                LocalMemoryBytes = properties.Limits.MaxComputeSharedMemorySize,
            });
        }
        return devices;
    }

    internal VulkanComputeApi(GpuDevice device, bool useDouble)
    {
        _vk = Shared.Value.Vk;
        _physical = new PhysicalDevice(device.Handle);
        _double = useDouble;
        uint count = 0;
        _vk.GetPhysicalDeviceQueueFamilyProperties(_physical, &count, null);
        var families = new QueueFamilyProperties[count];
        fixed (QueueFamilyProperties* p = families)
            _vk.GetPhysicalDeviceQueueFamilyProperties(_physical, &count, p);
        int family = Array.FindIndex(families, q => (q.QueueFlags & QueueFlags.ComputeBit) != 0);
        if (family < 0) throw new OpenClException("Vulkan device has no compute queue.");
        _family = (uint)family;
        float priority = 1;
        DeviceQueueCreateInfo queue = new() { SType = StructureType.DeviceQueueCreateInfo, QueueFamilyIndex = _family, QueueCount = 1, PQueuePriorities = &priority };
        PhysicalDeviceFeatures features = new() { ShaderFloat64 = useDouble };
        DeviceCreateInfo create = new() { SType = StructureType.DeviceCreateInfo, QueueCreateInfoCount = 1, PQueueCreateInfos = &queue, PEnabledFeatures = &features };
        Check(_vk.CreateDevice(_physical, in create, null, out _device), "create device");
        _vk.GetDeviceQueue(_device, _family, 0, out _queue);
        _vk.GetPhysicalDeviceMemoryProperties(_physical, out _memory);
        try
        {
            DescriptorSetLayoutBinding* bindings = stackalloc DescriptorSetLayoutBinding[6];
            for (uint i = 0; i < 6; i++)
                bindings[i] = new() { Binding = i, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit };
            DescriptorSetLayoutCreateInfo descriptorCreate = new() { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 6, PBindings = bindings };
            Check(_vk.CreateDescriptorSetLayout(_device, in descriptorCreate, null, out _descriptors), "descriptor layout");
            DescriptorSetLayout descriptors = _descriptors;
            PushConstantRange range = new() { StageFlags = ShaderStageFlags.ComputeBit, Size = 128 };
            PipelineLayoutCreateInfo layoutCreate = new() { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &descriptors, PushConstantRangeCount = 1, PPushConstantRanges = &range };
            Check(_vk.CreatePipelineLayout(_device, in layoutCreate, null, out _layout), "pipeline layout");
        }
        catch
        {
            _vk.DestroyDescriptorSetLayout(_device, _descriptors, null);
            _vk.DestroyDevice(_device, null);
            throw;
        }
    }

    public override nint CreateQueue(nint context, nint device)
    {
        CommandPool pool = default;
        DescriptorPool descriptors = default;
        Fence fence = default;
        try
        {
            CommandPoolCreateInfo poolCreate = new() { SType = StructureType.CommandPoolCreateInfo, QueueFamilyIndex = _family };
            Check(_vk.CreateCommandPool(_device, in poolCreate, null, out pool), "command pool");
            CommandBufferAllocateInfo allocate = new() { SType = StructureType.CommandBufferAllocateInfo, CommandPool = pool, Level = CommandBufferLevel.Primary, CommandBufferCount = 1 };
            Check(_vk.AllocateCommandBuffers(_device, in allocate, out CommandBuffer buffer), "command buffer");
            DescriptorPoolSize size = new() { Type = DescriptorType.StorageBuffer, DescriptorCount = 128 * 6 };
            DescriptorPoolCreateInfo descriptorCreate = new() { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 128, PoolSizeCount = 1, PPoolSizes = &size };
            Check(_vk.CreateDescriptorPool(_device, in descriptorCreate, null, out descriptors), "descriptor pool");
            FenceCreateInfo fenceCreate = new() { SType = StructureType.FenceCreateInfo };
            Check(_vk.CreateFence(_device, in fenceCreate, null, out fence), "fence");
            return Hold(new Commands(pool, buffer, descriptors, fence));
        }
        catch
        {
            _vk.DestroyFence(_device, fence, null);
            _vk.DestroyDescriptorPool(_device, descriptors, null);
            _vk.DestroyCommandPool(_device, pool, null);
            throw;
        }
    }

    public override nint CreateKernel(nint program, string name)
    {
        string resource = $"FUPlayer.Core.Gpu.{name}{(_double ? 64 : 32)}.spv";
        using Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new OpenClException($"Vulkan kernels are missing: {resource}");
        var code = new byte[source.Length];
        source.ReadExactly(code);
        fixed (byte* p = code)
        {
            ShaderModuleCreateInfo create = new() { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)code.Length, PCode = (uint*)p };
            Check(_vk.CreateShaderModule(_device, in create, null, out ShaderModule module), "shader module");
            return Hold(new Kernel(name, module));
        }
    }

    private Allocation Allocate(ulong bytes, bool host)
    {
        bytes = Math.Max(16, bytes);
        Buffer buffer = default;
        DeviceMemory memory = default;
        try
        {
            BufferCreateInfo create = new() { SType = StructureType.BufferCreateInfo, Size = bytes,
                Usage = BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit, SharingMode = SharingMode.Exclusive };
            Check(_vk.CreateBuffer(_device, in create, null, out buffer), "buffer");
            _vk.GetBufferMemoryRequirements(_device, buffer, out MemoryRequirements required);
            MemoryPropertyFlags flags = host ? MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit : MemoryPropertyFlags.DeviceLocalBit;
            uint type = uint.MaxValue;
            for (uint i = 0; i < _memory.MemoryTypeCount; i++)
                if ((required.MemoryTypeBits & (1u << (int)i)) != 0 && (_memory.MemoryTypes[(int)i].PropertyFlags & flags) == flags)
                { type = i; break; }
            if (type == uint.MaxValue) throw new OpenClException($"Vulkan has no {flags} memory for this buffer.");
            MemoryAllocateInfo allocate = new() { SType = StructureType.MemoryAllocateInfo, AllocationSize = required.Size, MemoryTypeIndex = type };
            Check(_vk.AllocateMemory(_device, in allocate, null, out memory), "memory");
            Check(_vk.BindBufferMemory(_device, buffer, memory, 0), "bind memory");
            return new Allocation(buffer, memory, bytes);
        }
        catch
        {
            _vk.DestroyBuffer(_device, buffer, null);
            _vk.FreeMemory(_device, memory, null);
            throw;
        }
    }
    public override nint CreateBuffer(nint context, ulong flags, nuint bytes) => Hold(Allocate(bytes, host: false));
    private void Free(Allocation allocation)
    {
        _vk.DestroyBuffer(_device, allocation.Buffer, null);
        _vk.FreeMemory(_device, allocation.Memory, null);
    }
    private Allocation Staging(Commands commands, ulong bytes)
    {
        if (commands.Staging is { } existing && existing.Size >= bytes) return existing;
        if (commands.Staging is not null) Free(commands.Staging);
        return commands.Staging = Allocate(bytes, host: true);
    }
    private CommandBuffer Begin(Commands commands)
    {
        if (commands.Pending) FinishCommands(commands);
        if (!commands.Recording)
        {
            CommandBufferBeginInfo begin = new() { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
            Check(_vk.BeginCommandBuffer(commands.Buffer, in begin), "begin commands");
            commands.Recording = true;
        }
        return commands.Buffer;
    }
    private void Barrier(CommandBuffer buffer)
    {
        MemoryBarrier barrier = new() { SType = StructureType.MemoryBarrier, SrcAccessMask = AccessFlags.MemoryWriteBit, DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit };
        _vk.CmdPipelineBarrier(buffer, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit, 0, 1, &barrier, 0, null, 0, null);
    }
    private void Copy(Commands commands, Allocation source, ulong sourceOffset, Allocation destination, ulong destinationOffset, ulong bytes)
    {
        CommandBuffer buffer = Begin(commands);
        Barrier(buffer);
        BufferCopy region = new() { SrcOffset = sourceOffset, DstOffset = destinationOffset, Size = bytes };
        _vk.CmdCopyBuffer(buffer, source.Buffer, destination.Buffer, 1, &region);
    }
    public override void Write(nint queue, nint buffer, nuint offsetBytes, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        Commands commands = Get<Commands>(queue);
        FinishCommands(commands);
        Allocation staging = Staging(commands, (ulong)data.Length);
        void* mapped = null;
        Check(_vk.MapMemory(_device, staging.Memory, 0, (ulong)data.Length, 0, &mapped), "map upload");
        data.CopyTo(new Span<byte>(mapped, data.Length));
        _vk.UnmapMemory(_device, staging.Memory);
        Copy(commands, staging, 0, Get<Allocation>(buffer), offsetBytes, (ulong)data.Length);
        FinishCommands(commands);
    }
    public override void Read(nint queue, nint buffer, nuint offsetBytes, Span<byte> data)
    {
        if (data.IsEmpty) return;
        Commands commands = Get<Commands>(queue);
        FinishCommands(commands);
        Allocation staging = Staging(commands, (ulong)data.Length);
        Copy(commands, Get<Allocation>(buffer), offsetBytes, staging, 0, (ulong)data.Length);
        FinishCommands(commands);
        void* mapped = null;
        Check(_vk.MapMemory(_device, staging.Memory, 0, (ulong)data.Length, 0, &mapped), "map download");
        new ReadOnlySpan<byte>(mapped, data.Length).CopyTo(data);
        _vk.UnmapMemory(_device, staging.Memory);
    }
    public override void Copy(nint queue, nint source, nuint sourceOffset, nint destination, nuint destinationOffset, nuint bytes) =>
        Copy(Get<Commands>(queue), Get<Allocation>(source), sourceOffset, Get<Allocation>(destination), destinationOffset, bytes);
    public override void SetArg(nint kernel, uint index, nint buffer) => Get<Kernel>(kernel).Arguments[index] = (ulong)buffer;
    public override void SetArg(nint kernel, uint index, int value) => Get<Kernel>(kernel).Arguments[index] = (uint)value;
    public override void SetArg(nint kernel, uint index, float value) => Get<Kernel>(kernel).Arguments[index] = BitConverter.SingleToUInt32Bits(value);
    public override void SetArg(nint kernel, uint index, double value) => Get<Kernel>(kernel).Arguments[index] = BitConverter.DoubleToUInt64Bits(value);
    public override void SetLocalArg(nint kernel, uint index, nuint bytes) { }

    private Pipeline PipelineFor(Kernel kernel, uint width)
    {
        if (kernel.Pipelines.TryGetValue(width, out Pipeline pipeline)) return pipeline;
        SpecializationMapEntry entry = new() { ConstantID = 0, Offset = 0, Size = sizeof(uint) };
        SpecializationInfo specialization = new() { MapEntryCount = 1, PMapEntries = &entry, DataSize = sizeof(uint), PData = &width };
        byte* main = stackalloc byte[5] { (byte)'m', (byte)'a', (byte)'i', (byte)'n', 0 };
        PipelineShaderStageCreateInfo stage = new() { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.ComputeBit, Module = kernel.Module, PName = main, PSpecializationInfo = &specialization };
        ComputePipelineCreateInfo create = new() { SType = StructureType.ComputePipelineCreateInfo, Stage = stage, Layout = _layout };
        Check(_vk.CreateComputePipelines(_device, default, 1, in create, null, out pipeline), "compute pipeline");
        kernel.Pipelines.Add(width, pipeline);
        return pipeline;
    }
    public override void Run(nint queue, nint kernel, nuint globalX, nuint globalY, nuint groupX)
    {
        Commands commands = Get<Commands>(queue);
        Kernel k = Get<Kernel>(kernel);
        Pipeline pipeline = PipelineFor(k, checked((uint)groupX));
        CommandBuffer buffer = Begin(commands);
        DescriptorSetLayout layout = _descriptors;
        DescriptorSetAllocateInfo allocate = new() { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = commands.Descriptors, DescriptorSetCount = 1, PSetLayouts = &layout };
        Check(_vk.AllocateDescriptorSets(_device, in allocate, out DescriptorSet set), "descriptor set");
        int buffers = k.Name switch { "fft_pass" or "accumulate" => 6, "direct" => 3, _ => 2 };
        DescriptorBufferInfo* infos = stackalloc DescriptorBufferInfo[6];
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[6];
        for (int i = 0; i < 6; i++)
        {
            Allocation data = Get<Allocation>((nint)k.Arguments[i < buffers ? i : 0]);
            infos[i] = new() { Buffer = data.Buffer, Range = data.Size };
            writes[i] = new() { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = (uint)i, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = infos + i };
        }
        _vk.UpdateDescriptorSets(_device, 6, writes, 0, null);
        Barrier(buffer);
        _vk.CmdBindPipeline(buffer, PipelineBindPoint.Compute, pipeline);
        _vk.CmdBindDescriptorSets(buffer, PipelineBindPoint.Compute, _layout, 0, 1, &set, 0, null);
        k.Arguments[12] = checked((uint)globalX) | ((ulong)checked((uint)globalY) << 32);
        fixed (ulong* p = k.Arguments)
            _vk.CmdPushConstants(buffer, _layout, ShaderStageFlags.ComputeBit, 0, 128, p);
        _vk.CmdDispatch(buffer, checked((uint)((globalX + groupX - 1) / groupX)), checked((uint)globalY), 1);
    }
    private void Submit(Commands commands)
    {
        if (!commands.Recording) return;
        Check(_vk.EndCommandBuffer(commands.Buffer), "end commands");
        CommandBuffer buffer = commands.Buffer;
        SubmitInfo submit = new() { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &buffer };
        lock (_submitLock) Check(_vk.QueueSubmit(_queue, 1, &submit, commands.Fence), "submit");
        commands.Recording = false;
        commands.Pending = true;
    }
    private void FinishCommands(Commands commands)
    {
        Submit(commands);
        if (!commands.Pending) return;
        Fence fence = commands.Fence;
        Check(_vk.WaitForFences(_device, 1, &fence, true, 30_000_000_000), "wait fence");
        Check(_vk.ResetFences(_device, 1, &fence), "reset fence");
        Check(_vk.ResetCommandPool(_device, commands.Pool, 0), "reset commands");
        Check(_vk.ResetDescriptorPool(_device, commands.Descriptors, 0), "reset descriptors");
        commands.Pending = false;
    }
    public override void Finish(nint queue)
    {
        if (queue != 0) FinishCommands(Get<Commands>(queue));
    }
    public override void Flush(nint queue) => Submit(Get<Commands>(queue));
    public override void ReleaseMemory(nint buffer)
    {
        if (buffer == 0) return;
        Free(Get<Allocation>(buffer));
        GCHandle.FromIntPtr(buffer).Free();
    }
    public override void ReleaseKernel(nint kernel)
    {
        if (kernel == 0) return;
        Kernel k = Get<Kernel>(kernel);
        foreach (Pipeline pipeline in k.Pipelines.Values) _vk.DestroyPipeline(_device, pipeline, null);
        _vk.DestroyShaderModule(_device, k.Module, null);
        GCHandle.FromIntPtr(kernel).Free();
    }
    public override void ReleaseQueue(nint queue)
    {
        if (queue == 0) return;
        Commands commands = Get<Commands>(queue);
        FinishCommands(commands);
        if (commands.Staging is not null) Free(commands.Staging);
        _vk.DestroyFence(_device, commands.Fence, null);
        _vk.DestroyDescriptorPool(_device, commands.Descriptors, null);
        _vk.DestroyCommandPool(_device, commands.Pool, null);
        GCHandle.FromIntPtr(queue).Free();
    }
}
