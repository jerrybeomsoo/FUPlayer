# Neural restorer

Enable **Neural restorer** in DSP studio to process supported 44.1/48 kHz stereo material at its source
rate. It attempts to reduce coding artefacts and reconstruct missing detail. It estimates that content;
it cannot recover the original recording exactly.

With **Source type → Automatic**, lossless files normally leave this stage idle. Choose Lossy explicitly
when testing a decoded lossy recording stored in WAV/FLAC. The selected stage and its active/idle status
are shown in Now playing. The supplied model and description are in the release's `models` folder.

## On a graphics adapter

The local **Runs on** selector chooses the processor or a Windows graphics adapter through DirectML.
This is independent of CUDA/Vulkan resampling. If an adapter cannot open, check the status explanation
and try the processor. The Windows system supplies DirectML; the release does not include DirectML.dll.

For another computer, enable the restorer checkbox in [Network DSP](network-dsp.md). Its local Runs on
selector becomes grey and its saved local choice is retained. The Linux server runs neural inference on
its CPU; CUDA/Vulkan accelerate convolution, not these neural models.

## The oversampling exciter

The [exciter](exciter.md) is now a separate optional stage. It can run with or without the restorer.
