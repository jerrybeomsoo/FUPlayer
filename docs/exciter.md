# Oversampling exciter

The exciter adds harmonics derived from the music's upper band. It operates quietly under existing
high-frequency content and can extend above the source's spectral edge when the output rate allows it.
It creates new content; it is not a reconstruction of missing original samples.

Enable **Oversampling exciter** in DSP studio. There are no additional controls. It can run with or
without the neural restorer and upscaler, and at output rates that do not include a neural doubling stage.
Inspect the processed spectrum or Output delta if you want to see what it adds. Turn Output delta off
for ordinary playback.

The exciter stays on the player when Network DSP is enabled. If playback overloads, compare processing
with this stage disabled along with the other optional stages. See [troubleshooting](troubleshooting.md).
