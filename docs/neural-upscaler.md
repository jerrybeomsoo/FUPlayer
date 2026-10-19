# Neural upscaler

Enable **Neural upscaler** in DSP studio for supported 44.1/48 kHz material. It produces 88.2/96 kHz
audio before final resampling. When both networks are enabled, it follows the restorer.
The extra band is an estimate made by the model, not recovered original high-frequency information.

**Upscaled band level** changes the level above the source Nyquist frequency. Start with Measured;
higher settings add more of that band. Processing adds delay and needs more computation.
Use Now playing's output spectrum or Output delta to inspect the result.

The release includes the model and its JSON description in `models`. Keep them together.
The local **Runs on** choice uses the processor or Windows DirectML; it is separate from resampling's
CUDA/Vulkan device selection. A [Network DSP](network-dsp.md) upscaler checkbox sends this stage to the
server and greys its local selector. Linux neural inference runs on the CPU.
