using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenDEDHub
{
    internal static class Program
    {
        static async Task Main(string[] args)
        {
            Console.Title = "OpenDEDHub (Standalone Self-Healing)";
            Log("OpenDEDHub started.");
            Log("Controls: Press [R] to force-reset USB port | Press [Q] to quit.");

            using var cts = new CancellationTokenSource();
            using var controller = new DedController(Log, ignoreDcsWhenBothRunning: true);

            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            Task keyboardTask = Task.Run(() => RunKeyboardListener(controller, cts));
            Task dcsTask = Task.Run(() => controller.RunDcsUdpListener(cts.Token));

            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    if (!controller.IsDeviceConnected)
                    {
                        if (!controller.DiscoverAndConnect())
                        {
                            await Task.Delay(1500, cts.Token);
                            continue;
                        }
                    }

                    OperationMode targetMode = controller.DetermineTargetMode();
                    if (targetMode != controller.CurrentMode)
                    {
                        controller.SwitchMode(targetMode);
                    }

                    if (controller.CurrentMode == OperationMode.BMS && controller.IsDeviceConnected)
                    {
                        controller.PushBmsDedFrame();
                        await Task.Delay(33, cts.Token); // 30 Hz smooth DED refresh
                    }
                    else
                    {
                        await Task.Delay(500, cts.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log($"Unexpected loop error: {ex.Message}");
                    controller.RecoverFromUsbLockup("Unhandled loop exception");
                    await Task.Delay(1000, cts.Token);
                }
            }

            Log("Shutting down...");
        }

        private static void RunKeyboardListener(DedController controller, CancellationTokenSource cts)
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    if (Console.KeyAvailable)
                    {
                        ConsoleKeyInfo keyInfo = Console.ReadKey(intercept: true);
                        if (keyInfo.Key == ConsoleKey.R)
                        {
                            controller.RecoverFromUsbLockup("Manual [R] force-reset triggered by user");
                        }
                        else if (keyInfo.Key == ConsoleKey.Q)
                        {
                            cts.Cancel();
                            break;
                        }
                    }
                    Thread.Sleep(50);
                }
                catch
                {
                    Thread.Sleep(500);
                }
            }
        }

        private static void Log(string message)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
        }
    }
}
