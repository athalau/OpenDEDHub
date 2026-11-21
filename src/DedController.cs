using System;
using System.Diagnostics;
using System.IO.Ports;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using F4SharedMem;

namespace OpenDEDHub
{
    internal sealed class DedController : IDisposable
    {
        private readonly Reader _bmsReader = new Reader();
        private readonly SemaphoreSlim _recoveryLock = new SemaphoreSlim(1, 1);
        private readonly Action<string> _log;
        private readonly bool _ignoreDcsWhenBothRunning;

        private SerialPort _serialPort;
        private OperationMode _currentMode = OperationMode.WAIT;
        private volatile bool _deviceConnected = false;

        private string[] _lastDedLines = Array.Empty<string>();
        private string[] _lastInvertLines = Array.Empty<string>();

        public bool IsDeviceConnected => _deviceConnected && _serialPort != null && _serialPort.IsOpen;
        public OperationMode CurrentMode => _currentMode;

        public DedController(Action<string> log, bool ignoreDcsWhenBothRunning = true)
        {
            _log = log ?? Console.WriteLine;
            _ignoreDcsWhenBothRunning = ignoreDcsWhenBothRunning;
        }

        public bool DiscoverAndConnect()
        {
            if (_recoveryLock.CurrentCount == 0)
                return false;

            string[] ports = SerialPort.GetPortNames();
            byte[] idCmd = Protocol.ComposeTextCommand('I');

            foreach (string portName in ports)
            {
                SafeCloseSerialPort();

                var candidate = new SerialPort(portName, Protocol.BaudRate, Parity.None, 8, StopBits.One)
                {
                    DtrEnable = true,
                    RtsEnable = true,
                    WriteTimeout = 250,
                    ReadTimeout = 250
                };

                try
                {
                    candidate.Open();
                    candidate.DiscardInBuffer();
                    candidate.Write(idCmd, 0, idCmd.Length);

                    byte[] replyBuf = new byte[Protocol.IDReplyLen];
                    int totalRead = 0;
                    var sw = Stopwatch.StartNew();

                    while (totalRead < Protocol.IDReplyLen && sw.ElapsedMilliseconds < 400)
                    {
                        if (candidate.BytesToRead > 0)
                        {
                            int read = candidate.Read(replyBuf, totalRead, replyBuf.Length - totalRead);
                            totalRead += read;
                        }
                        else
                        {
                            Thread.Sleep(5);
                        }
                    }

                    if (Protocol.TryParseIdentification(replyBuf, totalRead, out byte hwRev, out byte fwMaj, out byte fwMin))
                    {
                        _serialPort = candidate;
                        _deviceConnected = true;
                        _currentMode = OperationMode.WAIT;
                        _log($"Connected to SimGears DED (HW v{hwRev}, FW v{fwMaj}.{fwMin}) on {portName}");
                        SendCommandSafe(Protocol.ComposeTextCommand('W'));
                        return true;
                    }
                }
                catch
                {
                    // Skip non-DED or busy ports
                }

                try { if (candidate.IsOpen) candidate.Close(); candidate.Dispose(); } catch { }
            }

            return false;
        }

        public OperationMode DetermineTargetMode()
        {
            bool bmsRunning = IsFalconBmsRunning();
            bool dcsRunning = IsDcsRunning();

            if (bmsRunning && dcsRunning)
                return _ignoreDcsWhenBothRunning ? OperationMode.BMS : OperationMode.WAIT;
            if (bmsRunning)
                return OperationMode.BMS;
            if (dcsRunning)
                return OperationMode.DCS;
            return OperationMode.WAIT;
        }

        public void SwitchMode(OperationMode newMode)
        {
            char modeChar = newMode switch
            {
                OperationMode.BMS => 'B',
                OperationMode.DCS => 'D',
                _ => 'W'
            };

            _log($"Switching mode: {_currentMode} -> {newMode}");
            if (SendCommandSafe(Protocol.ComposeTextCommand(modeChar)))
            {
                _currentMode = newMode;
                _lastDedLines = Array.Empty<string>();
                _lastInvertLines = Array.Empty<string>();
            }
        }

        public void PushBmsDedFrame()
        {
            FlightData data = _bmsReader.GetCurrentData();
            if (data?.DEDLines == null || data.Invert == null || data.DEDLines.Length < 5)
                return;

            if (data.DEDLines.Any(l => string.IsNullOrEmpty(l)))
                return;

            bool linesChanged = !data.DEDLines.SequenceEqual(_lastDedLines);
            bool invertChanged = !data.Invert.SequenceEqual(_lastInvertLines);

            if (linesChanged || invertChanged)
            {
                _lastDedLines = (string[])data.DEDLines.Clone();
                _lastInvertLines = (string[])data.Invert.Clone();

                byte[] fontPayload = Protocol.ProcessDedLines(_lastDedLines, _lastInvertLines);
                byte[] packet = Protocol.ComposeDisplayCommand(fontPayload);
                SendCommandSafe(packet);
            }
        }

        public void RunDcsUdpListener(CancellationToken token)
        {
            UdpClient udpClient = null;
            try
            {
                udpClient = new UdpClient();
                udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udpClient.ExclusiveAddressUse = false;
                udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, Protocol.DcsMulticastPort));
                udpClient.JoinMulticastGroup(IPAddress.Parse(Protocol.DcsMulticastIp));

                IPEndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);
                while (!token.IsCancellationRequested)
                {
                    if (_currentMode == OperationMode.DCS && IsDeviceConnected && udpClient.Available > 0)
                    {
                        byte[] dcsPacket = udpClient.Receive(ref remoteEp);
                        SendCommandSafe(dcsPacket);
                    }
                    else
                    {
                        Thread.Sleep(10);
                    }
                }
            }
            catch (Exception ex)
            {
                _log($"DCS Multicast listener warning: {ex.Message}");
            }
            finally
            {
                try { udpClient?.Dispose(); } catch { }
            }
        }

        public void RecoverFromUsbLockup(string reason)
        {
            if (!_recoveryLock.Wait(0))
            {
                // Recovery is already in progress
                return;
            }

            try
            {
                _log($"Recovery triggered: {reason}");
                _deviceConnected = false;
                _currentMode = OperationMode.WAIT;
                SafeCloseSerialPort();

                UsbPortResetter.ResetDedUsbDevice(_log);

                // Give the RP2040 TinyUSB stack time to re-enumerate its COM port
                Thread.Sleep(2500);
            }
            finally
            {
                _recoveryLock.Release();
            }
        }

        private bool SendCommandSafe(byte[] data, bool allowRecovery = true)
        {
            if (_recoveryLock.CurrentCount == 0)
                return false;

            if (_serialPort == null || !_serialPort.IsOpen)
            {
                if (allowRecovery) RecoverFromUsbLockup("Serial port closed unexpectedly");
                return false;
            }

            try
            {
                _serialPort.Write(data, 0, data.Length);
                return true;
            }
            catch (Exception ex)
            {
                if (allowRecovery) RecoverFromUsbLockup($"Serial write failed ({ex.Message})");
                return false;
            }
        }

        private void SafeCloseSerialPort()
        {
            if (_serialPort != null)
            {
                try { if (_serialPort.IsOpen) _serialPort.Close(); } catch { }
                try { _serialPort.Dispose(); } catch { }
                _serialPort = null;
            }
        }

        private bool IsFalconBmsRunning()
        {
            try
            {
                return Process.GetProcessesByName("Falcon BMS").Length > 0 && _bmsReader.IsFalconRunning;
            }
            catch { return false; }
        }

        private static bool IsDcsRunning()
        {
            try
            {
                return Process.GetProcessesByName("DCS").Length > 0;
            }
            catch { return false; }
        }

        public void Dispose()
        {
            SendCommandSafe(Protocol.ComposeTextCommand('W'), allowRecovery: false);
            SafeCloseSerialPort();
            _recoveryLock.Dispose();
        }
    }
}
