namespace FUPlayer.Core.Dsp.Acceleration;

/// <summary>The driver API used to execute convolution kernels.</summary>
public enum GpuBackend
{
    OpenCl,
    Cuda,
    Vulkan,
}

/// <summary>In-order compute queues shared by the convolution implementations.</summary>
internal abstract class ComputeApi
{
    public abstract nint CreateQueue(nint context, nint device);
    public abstract nint CreateKernel(nint program, string name);
    public abstract nint CreateBuffer(nint context, ulong flags, nuint bytes);
    public abstract void Write(nint queue, nint buffer, nuint offsetBytes, ReadOnlySpan<byte> data);
    public virtual nint WriteAsync(nint queue, nint buffer, nuint offsetBytes, ReadOnlySpan<byte> data)
    {
        Write(queue, buffer, offsetBytes, data);
        return 0;
    }
    public virtual void Wait(nint completed) { }
    public virtual void ReleaseEvent(nint completed) { }
    public abstract void Read(nint queue, nint buffer, nuint offsetBytes, Span<byte> data);
    public void Copy(nint queue, nint source, nint destination, nuint bytes) => Copy(queue, source, 0, destination, 0, bytes);
    public abstract void Copy(nint queue, nint source, nuint sourceOffset, nint destination, nuint destinationOffset, nuint bytes);
    public abstract void SetArg(nint kernel, uint index, nint buffer);
    public abstract void SetArg(nint kernel, uint index, int value);
    public abstract void SetArg(nint kernel, uint index, float value);
    public abstract void SetArg(nint kernel, uint index, double value);
    public abstract void SetLocalArg(nint kernel, uint index, nuint bytes);
    public void Run(nint queue, nint kernel, nuint globalX, nuint globalY) => Run(queue, kernel, globalX, globalY, Math.Min(64, globalX));
    public abstract void Run(nint queue, nint kernel, nuint globalX, nuint globalY, nuint groupX);
    public abstract void Finish(nint queue);
    public abstract void Flush(nint queue);
    public abstract void ReleaseMemory(nint buffer);
    public abstract void ReleaseKernel(nint kernel);
    public abstract void ReleaseQueue(nint queue);
}

internal sealed class OpenClComputeApi : ComputeApi
{
    public static readonly OpenClComputeApi Instance = new();
    public override nint CreateQueue(nint context, nint device) => OpenClApi.CreateQueue(context, device);
    public override nint CreateKernel(nint program, string name) => OpenClApi.CreateKernel(program, name);
    public override nint CreateBuffer(nint context, ulong flags, nuint bytes) => OpenClApi.CreateBuffer(context, flags, bytes);
    public override void Write(nint queue, nint buffer, nuint offsetBytes, ReadOnlySpan<byte> data) => OpenClApi.Write(queue, buffer, offsetBytes, data);
    public override nint WriteAsync(nint queue, nint buffer, nuint offsetBytes, ReadOnlySpan<byte> data) => OpenClApi.WriteAsync(queue, buffer, offsetBytes, data);
    public override void Wait(nint completed) => OpenClApi.Wait(completed);
    public override void ReleaseEvent(nint completed) => OpenClApi.ReleaseEvent(completed);
    public override void Read(nint queue, nint buffer, nuint offsetBytes, Span<byte> data) => OpenClApi.Read(queue, buffer, offsetBytes, data);
    public override void Copy(nint queue, nint source, nuint sourceOffset, nint destination, nuint destinationOffset, nuint bytes) => OpenClApi.Copy(queue, source, sourceOffset, destination, destinationOffset, bytes);
    public override void SetArg(nint kernel, uint index, nint buffer) => OpenClApi.SetArg(kernel, index, buffer);
    public override void SetArg(nint kernel, uint index, int value) => OpenClApi.SetArg(kernel, index, value);
    public override void SetArg(nint kernel, uint index, float value) => OpenClApi.SetArg(kernel, index, value);
    public override void SetArg(nint kernel, uint index, double value) => OpenClApi.SetArg(kernel, index, value);
    public override void SetLocalArg(nint kernel, uint index, nuint bytes) => OpenClApi.SetLocalArg(kernel, index, bytes);
    public override void Run(nint queue, nint kernel, nuint globalX, nuint globalY, nuint groupX) => OpenClApi.Run(queue, kernel, globalX, globalY, groupX);
    public override void Finish(nint queue) => OpenClApi.Finish(queue);
    public override void Flush(nint queue) => OpenClApi.Flush(queue);
    public override void ReleaseMemory(nint buffer) => OpenClApi.ReleaseMemory(buffer);
    public override void ReleaseKernel(nint kernel) => OpenClApi.ReleaseKernel(kernel);
    public override void ReleaseQueue(nint queue) => OpenClApi.ReleaseQueue(queue);
}
