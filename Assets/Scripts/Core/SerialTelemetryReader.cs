using System;
using System.Collections.Concurrent;
using System.IO.Ports;
using System.Threading;

public class SerialTelemetryReader : IDisposable
{
    private SerialPort _serialPort;
    private Thread _readThread;
    private bool _isRunning;
    public ConcurrentQueue<string> rawDataQueue;

    public SerialTelemetryReader(string portNames, int baudRate)
    {
        rawDataQueue = new ConcurrentQueue<string>();

        string[] ports = portNames.Split(',');
        bool connected = false;

        foreach (string rawPort in ports)
        {
            string port = rawPort.Trim();
            if (string.IsNullOrEmpty(port)) continue;

            try
            {
                _serialPort = new SerialPort(port, baudRate);
                _serialPort.NewLine = "\n";
                _serialPort.DtrEnable = true;
                _serialPort.RtsEnable = true;
                _serialPort.Handshake = Handshake.None;
                _serialPort.ReadTimeout = 200;
                _serialPort.WriteTimeout = 200;
                _serialPort.Open();
                _serialPort.DiscardInBuffer();

                _isRunning = true;
                _readThread = new Thread(ReadLoop) { IsBackground = true };
                _readThread.Start();

                UnityEngine.Debug.Log($"[SerialTelemetryReader] Successfully connected to {port} at {baudRate} baud.");
                connected = true;
                break;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[SerialTelemetryReader] Could not open {port}: {ex.Message}. Trying next...");
                if (_serialPort != null)
                {
                    try { _serialPort.Dispose(); } catch { }
                    _serialPort = null;
                }
            }
        }

        if (!connected)
        {
            UnityEngine.Debug.LogError($"[SerialTelemetryReader] Failed to open any port from: {portNames}");
        }
    }

    private void ReadLoop()
    {
        while (_isRunning && _serialPort != null && _serialPort.IsOpen)
        {
            try
            {
                string line = _serialPort.ReadLine();
                if (!string.IsNullOrEmpty(line))
                {
                    rawDataQueue.Enqueue(line);
                }
            }
            catch (TimeoutException)
            {
                // Normal timeout, loop again
            }
            catch (Exception)
            {
                // Ignore unexpected thread errors to keep it alive
            }
        }
    }

    public void Stop()
    {
        _isRunning = false;
        if (_readThread != null && _readThread.IsAlive)
        {
            _readThread.Join(500);
        }
        
        if (_serialPort != null)
        {
            if (_serialPort.IsOpen)
            {
                _serialPort.Close();
            }
            _serialPort.Dispose();
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
