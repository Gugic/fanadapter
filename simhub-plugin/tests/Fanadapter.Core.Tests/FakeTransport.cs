using System;
using System.Collections.Generic;
using Fanadapter.Core;

namespace Fanadapter.Core.Tests
{
    /// <summary>
    /// In-memory stand-in for a COM port: records what the client wrote and lets
    /// a test push arbitrary bytes back, including at deliberately awkward chunk
    /// boundaries.
    /// </summary>
    internal class FakeTransport : ISerialTransport
    {
        public List<string> Written { get; } = new List<string>();
        public bool IsOpen { get; private set; }
        public Exception ThrowOnWrite { get; set; }

        public event Action<string> DataReceived;
        public event Action<Exception> ErrorOccurred;

        public void Open() => IsOpen = true;
        public void Close() => IsOpen = false;
        public void Dispose() => Close();

        public void Write(string text)
        {
            if (ThrowOnWrite != null) throw ThrowOnWrite;
            Written.Add(text);
        }

        /// <summary>Deliver text from the device exactly as given — no line framing.</summary>
        public void Receive(string text) => DataReceived?.Invoke(text);

        /// <summary>Deliver a complete line, terminator included.</summary>
        public void ReceiveLine(string line) => Receive(line + "\n");

        public void Fail(Exception ex) => ErrorOccurred?.Invoke(ex);
    }
}
