# Windows DSP server controller

`FUPlayer-DspServer-Windows.exe` is a Windows Forms application for a processing-only
Windows computer. It includes the server, runtime and both neural models in one
portable executable. FUPlayer and Firefox are not needed on that computer. The
existing console executable and Linux server remain available separately.

## Start a server

1. Open the executable and choose **Connection**.
2. Leave **Listen on IP** at `0.0.0.0` to accept IPv4 connections on the computer's
   interfaces. The default **UDP listener port** is `58181`.
3. Click **Start server**. **Listening** means the port was bound successfully;
   a bind conflict is reported as an error.
4. In FUPlayer, enter the server computer's IP or domain and listener port in
   **Settings → Network DSP**, then select the processing stages to offload.

The Windows firewall must permit inbound UDP on the listener port. For Internet
connections, forward that single UDP port on the router. Worker ports are bound
to loopback and do not need forwarding. The controller does not modify firewall
rules or router settings.

Private LAN connections can use plain UDP with **Allow Internet connections**
cleared and no key file selected. For Internet use, enable that option, choose
**Create key** or browse to an existing server key, then use **Copy access key**
to put the same key in FUPlayer. A configured key enables authenticated encrypted
UDP for LAN peers too. There is no TLS. Creating a key never overwrites a file.

## Processing and status

**Processing** sets the helper-thread budget, convolution device and precision,
and the restorer/upscaler adapters. Automatic convolution prefers CUDA for NVIDIA
and Vulkan for other GPUs; neural GPU processing uses Windows DirectML. The
64-bit arithmetic option is enabled by default. Changing precision is explicit.
Choose **Refresh GPU list** to discover named devices in a short-lived process;
the idle controller never opens a GPU context just to populate the interface.

The client chooses filters, taps, neural enablement, server FIFO and final DSD
conversion. The controller sets the resources available to those sessions. Stop
the server before changing connection or processing choices. Saved settings apply
on the next server start.

Status updates once a second. Connected clients includes opening sessions. CPU is
the listener and workers' share of the computer's total logical CPU capacity,
not FUPlayer's real-time DSP percentage. Memory is the sum of their working sets;
shared pages can be counted more than once. **Activity** has a bounded local log
that can be saved manually. Access-key contents and audio are not logged.

An idle listener does not load neural models or initialise processing GPUs.
Workers release their resources after clients disconnect. The controller itself
retains its small Forms interface and status timer.

## Closing and saved settings

**Stop server** closes this controller's listener and its workers. Closing the
window normally also stops them. If **Keep running in the notification area when
closing** is enabled, closing hides the window and leaves the server running;
use the notification icon's **Exit and stop server** command to end it. A Windows
job owns the child process tree so a controller crash also terminates its server.
Other server instances are not adopted, stopped or reconfigured.

**Start the server when this controller opens** applies on later launches of the
controller. It does not install a Windows service or register a Windows login task.
Those are separate deployment choices.

Preferences are stored under
`%LOCALAPPDATA%\FUPlayer-DspServer\Controller\controller.json`. They contain a key
file path, never the key itself. Models and the .NET single-file runtime use
per-user extraction caches. Malformed settings are reported without overwriting
the file automatically. English and Korean follow the system language, with an
override for the next launch.

For an isolated preview:

```powershell
& '.\FUPlayer-DspServer-Windows.exe' --settings-dir 'D:\FUPlayer-controller-test' --language en
```

## Build

```powershell
.\build\publish-dsp-server-windows.ps1
```

Requires the .NET 9 SDK or a compatible newer SDK on Windows. Targets .NET 9 and
C# 13 for Visual Studio 2022 compatibility. Published output is self-contained;
the receiving computer does not need to install .NET. Embedded licence texts can
be opened through **Licences**. The Forms application reuses the same listener
implementation as the console server; local control/status is carried on redirected
standard input/output and does not change the network DSP protocol.
