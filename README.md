# OpenDEDHub

A lightweight, open-source, self-healing replacement for the official **Simgears F16 DED Hub** software, built in C# (.NET 10 LTS) for **Falcon BMS** (with **DCS World** multicast pass-through support).
You'll need a [Simgears F16 DED](https://www.simgears.com/products/f16-ded-data-entry-display/) for this to work.

## Why OpenDEDHub?

The official Simgears DED Hub (v0910) + DED Firmware (v1.0) suffers from a known issue during Falcon BMS sessions—especially when Alt-Tabbing or spending extended time in the 2D briefing screen—where the hardware's Raspberry Pi Pico serial interface stalls. When this happens:

1. The official `DEDHub.exe` throws an unhandled `System.IO.IOException` (`WinIOError`) modal because it attempts to call `SerialPort.Close()` on a dropped USB handle without exception handling.
2. The DED display freezes and stops responding until the USB port is power-cycled or re-enumerated, the error dialogs are dismissed, and the Hub is restarted and rescanned manually.

See [DEDWatchdog](https://github.com/athalau/DEDWatchdog/) for details.

**OpenDEDHub** solves both problems in a single standalone executable without relying on external PowerShell daemons or third-party USB reset utilities:
- **Zero-Crash Serial Handling:** Safely catches `IOException` and `TimeoutException` events without unhandled `.NET` popups.
- **Built-in Win32 USB Port Reset:** Uses native Windows SetupAPI (`DICS_PROPCHANGE`) and Configuration Manager (`CM_Reenumerate_DevNode`) calls to automatically cycle the Simgears DED USB port on a serial lockup and reconnect seamlessly within ~2.5 seconds.
- **Smooth 30 Hz DED Updates:** Polls Falcon BMS shared memory (`FalconSharedMemoryArea`) on a dedicated background loop every `33 ms` (compared to `200 ms` in the stock Hub) and only transmits frames when DED text or inverse-highlight flags change.
- **DCS-BIOS Friendly:** Binds to the DCS UDP multicast stream (`239.255.50.10:5010`) with `ReuseAddress = true` so it does not block other cockpit tools from sharing the port.
- **Manual Force-Reset Hotkey:** Press **`R`** in the console window at any time to force a USB port reset and re-handshake.

---

## Prerequisites

- **OS:** Windows 10 or Windows 11 (x64)
- **Runtime / SDK:** [.NET 10.0 SDK](https://microsoft.com) (to build from source) or .NET 10.0 Desktop Runtime (if running a framework-dependent binary).
- **Shared Memory Library:** `F4SharedMem.dll` (copy this from your existing Simgears DED Hub installation folder, typically `C:\Program Files (x86)\SimGears\DEDHub\F4SharedMem.dll`).

## Quick Start (Prebuilt Release)

If you don't want to compile from source, you can download a prebuilt binary from the [Releases](https://github.com) page:

1. Download and extract the latest release archive to a folder of your choice.
2. Copy `F4SharedMem.dll` from your existing Simgears DED Hub installation folder (typically `C:\Program Files (x86)\SimGears\DEDHub\F4SharedMem.dll`) into the same folder as `OpenDEDHub.exe`.
3. *(If using the framework-dependent build)* Make sure the **.NET 10.0 Desktop Runtime (x64)** is installed on your system. The standalone (`-standalone`) build requires no additional runtime.
4. Run `OpenDEDHub.exe` (see [Usage](#usage) below).

---

## Building from Source

1. Clone this repository:
   ```powershell
   git clone https://github.com/athalau/OpenDEDHub
   ```
2. Copy `F4SharedMem.dll` from your Simgears installation directory into the project root folder alongside `OpenDEDHub.csproj`.
3. Build and publish a single-file executable:
   ```powershell
   dotnet publish -c Release -r win-x64 --self-contained false
   ```
   *(Optional: Use `--self-contained true` to bundle the .NET runtime into a standalone `.exe` that runs on machines without .NET 10 installed).*
4. Your compiled binary will be located at:
   ```text
   bin\Release\net10.0-windows\win-x64\publish\OpenDEDHub.exe
   ```

---

## Usage

1. Make sure the official Simgears **DEDHub** is closed.
2. Place `F4SharedMem.dll` in the same directory as `OpenDEDHub.exe` (if not already copied during build).
3. Launch **`OpenDEDHub.exe`** (accept the UAC prompt—Administrator privileges are required by Windows to cycle USB device nodes via SetupAPI).
4. Launch **Falcon BMS** (or **DCS World**). `OpenDEDHub` will automatically detect the simulator, switch the DED into the appropriate mode, and begin streaming cockpit telemetry.

## Configuration (`config.ini`)

On first launch (or first USB reset), **OpenDEDHub** automatically generates a `config.ini` file in the same directory as `OpenDEDHub.exe` if one does not already exist.

```ini
; OpenDEDHub Configuration
; Find your Hardware ID in Windows Device Manager -> Simgears DED -> Properties -> Details -> Hardware Ids
[USB]
TargetVidPid=VID_2E8A&PID_000A
```

* **`TargetVidPid`** — The USB Vendor ID and Product ID used by the self-healing resetter to locate and power-cycle your Simgears DED on the USB bus. Defaults to `VID_2E8A&PID_000A` (Raspberry Pi Pico CDC).
* **Finding your ID:** If your hardware revision uses a different ID, open **Windows Device Manager**, right-click your **Simgears DED** device, select **Properties > Details**, choose **Hardware Ids** from the dropdown, and update `TargetVidPid` accordingly. Changes take effect on the next reset without needing to recompile.

### Console Controls
- **`R`** — Force an immediate USB port reset and COM port re-discovery (useful if the Pico display ever freezes silently without throwing a serial error).
- **`Q`** (or `Ctrl+C`) — Return the DED to Wait/Idle mode (`#|W`) and exit cleanly.

---

## Project Structure

- `Program.cs` — Application entry point, main telemetry loop, and keyboard listener.
- `DedController.cs` — COM port discovery, Falcon BMS shared-memory polling, DCS multicast UDP listener, and thread-safe recovery state machine.
- `Protocol.cs` — Simgears wire protocol (`115200` 8N1), command framing (`#|I`, `#|W`, `#|B`, `#|D`, `#|d`), and custom OLED glyph/inverse-character encoder.
- `UsbPortResetter.cs` — Native Win32 P/Invoke wrapper (`setupapi.dll` & `cfgmgr32.dll`) for resetting the device with the ID configured in `UsbPortResetter.cs`.
- `OperationMode.cs` — Operating state definitions (`WAIT`, `BMS`, `DCS`).
- `ConfigLoader.cs` — Creates and parses `config.ini` for user-configurable USB hardware IDs.
- `app.manifest` — Requests `requireAdministrator` execution level so USB resets execute without mid-flight UAC interruptions.

---

## Disclaimer & License

This is an independent community project and is not affiliated with or endorsed by Simgears. Use at your own risk.
