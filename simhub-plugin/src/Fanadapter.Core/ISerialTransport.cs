using System;
using System.IO.Ports;
using System.Text;
using System.Threading;

namespace Fanadapter.Core
{
    /// <summary>
    /// The byte pipe under <see cref="SerialClient"/>. Abstracted so the framing
    /// and request/response logic can be tested against a fake without a COM port.
    /// </summary>
    public interface ISerialTransport : IDisposable
    {
        bool IsOpen { get; }
        void Open();
        void Close();
        void Write(string text);

        /// <summary>Raw text as it arrives — arbitrary chunk boundaries, not lines.</summary>
        event Action<string> DataReceived;

        event Action<Exception> ErrorOccurred;
    }

    /// <summary>
    /// System.IO.Ports transport configured the way the firmware expects.
    /// </summary>
    public class SerialPortTransport : ISerialTransport
    {
        private readonly SerialPort _port;
        private Thread _reader;
        private volatile bool _running;

        public event Action<string> DataReceived;
        public event Action<Exception> ErrorOccurred;

        public string PortName { get; }

        public SerialPortTransport(string portName)
        {
            PortName = portName;

            // 8E1 at 115200. Even parity is what the STM32's USART1 console runs
            // (it shares the framing with the ROM bootloader so one bridge config
            // covers console and flashing); a native USB CDC ignores parity
            // entirely, so this setting is correct for every supported path.
            _port = new SerialPort(portName, 115200, Parity.Even, 8, StopBits.One)
            {
                Encoding = Encoding.UTF8,
                ReadTimeout = 250,
                WriteTimeout = 1000,

                // Deliberately deasserted. A USB-UART bridge resets the MCU behind
                // it when DTR asserts, and the firmware gates its output on
                // tud_mounted() rather than DTR precisely so this is safe.
                DtrEnable = false,
                RtsEnable = false,
            };
        }

        public bool IsOpen => _port.IsOpen;

        public void Open()
        {
            _port.Open();
            _port.DiscardInBuffer();
            _running = true;
            _reader = new Thread(ReadLoop)
            {
                IsBackground = true,
                Name = "fanadapter-serial-read",
            };
            _reader.Start();
        }

        private void ReadLoop()
        {
            var buffer = new byte[1024];
            var decoder = Encoding.UTF8.GetDecoder();
            var chars = new char[1024];

            while (_running)
            {
                int read;
                try
                {
                    read = _port.Read(buffer, 0, buffer.Length);
                }
                catch (TimeoutException)
                {
                    continue;
                }
                catch (Exception ex)
                {
                    // Unplugging the adapter lands here. Report once, then stop:
                    // the client turns this into a disconnect.
                    if (_running) ErrorOccurred?.Invoke(ex);
                    return;
                }

                if (read <= 0) continue;

                // Decode incrementally so a multi-byte sequence split across two
                // reads doesn't turn into replacement characters.
                int charCount = decoder.GetChars(buffer, 0, read, chars, 0);
                if (charCount > 0) DataReceived?.Invoke(new string(chars, 0, charCount));
            }
        }

        public void Write(string text)
        {
            _port.Write(text);
        }

        public void Close()
        {
            _running = false;
            try
            {
                if (_port.IsOpen) _port.Close();
            }
            catch
            {
                // Closing a port whose device already vanished throws; nothing
                // useful to do about it.
            }

            var reader = _reader;
            if (reader != null && reader.IsAlive) reader.Join(TimeSpan.FromSeconds(1));
            _reader = null;
        }

        public void Dispose()
        {
            Close();
            _port.Dispose();
        }
    }
}
