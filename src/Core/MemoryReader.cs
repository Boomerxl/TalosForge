using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using BlackMagic;

namespace TalosForge.Core
{
    // If BlackMagic is not installed in your project:
    // Install-Package BlackMagic
    public sealed class MemoryReader : IDisposable
    {
        // Known WoW 3.3.5a offsets (for future use):
        // CurMgrPointer = 0x00C79CE0
        // ObjectManagerOffset = 0x2ED0
        // FirstObjectOffset = 0x00AC
        // NextObjectOffset = 0x003C
        // LocalGuidOffset = 0x0030

        private readonly object _syncRoot = new object();
        private readonly string _processName;
        private readonly BlackMagic.BlackMagic _blackMagic;

        private Process? _attachedProcess;
        private bool _disposed;

        public MemoryReader(string processName = "Wow")
        {
            _processName = processName;
            _blackMagic = new BlackMagic.BlackMagic();
            Attach();
        }

        public int ReadInt(uint address)
        {
            lock (_syncRoot)
            {
                EnsureReady();
                return _blackMagic.ReadInt(address);
            }
        }

        public float ReadFloat(uint address)
        {
            lock (_syncRoot)
            {
                EnsureReady();
                return _blackMagic.ReadFloat(address);
            }
        }

        public byte ReadByte(uint address)
        {
            lock (_syncRoot)
            {
                EnsureReady();
                return _blackMagic.ReadByte(address);
            }
        }

        public uint ReadUInt(uint address)
        {
            lock (_syncRoot)
            {
                EnsureReady();
                return _blackMagic.ReadUInt(address);
            }
        }

        public string ReadString(uint address, int length = 256)
        {
            if (length <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length), "Length must be greater than zero.");
            }

            lock (_syncRoot)
            {
                EnsureReady();
                var data = _blackMagic.ReadBytes(address, length);
                var end = Array.IndexOf(data, (byte)0);
                var count = end >= 0 ? end : data.Length;
                return Encoding.ASCII.GetString(data, 0, count);
            }
        }

        public byte[] ReadBytes(uint address, int count)
        {
            if (count <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count), "Count must be greater than zero.");
            }

            lock (_syncRoot)
            {
                EnsureReady();
                return _blackMagic.ReadBytes(address, count);
            }
        }

        public void WriteInt(uint address, int value)
        {
            lock (_syncRoot)
            {
                EnsureReady();
                _blackMagic.WriteInt(address, value);
            }
        }

        public void WriteFloat(uint address, float value)
        {
            lock (_syncRoot)
            {
                EnsureReady();
                _blackMagic.WriteFloat(address, value);
            }
        }

        public uint GetModuleBase(string moduleName = "Wow.exe")
        {
            if (string.IsNullOrWhiteSpace(moduleName))
            {
                throw new ArgumentException("Module name cannot be empty.", nameof(moduleName));
            }

            lock (_syncRoot)
            {
                EnsureReady();

                foreach (ProcessModule module in _attachedProcess!.Modules)
                {
                    if (string.Equals(module.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase))
                    {
                        return unchecked((uint)module.BaseAddress.ToInt64());
                    }
                }

                throw new InvalidOperationException($"Module '{moduleName}' was not found in process '{_attachedProcess.ProcessName}'.");
            }
        }

        public uint ReadPointerChain(uint baseAddr, params int[] offsets)
        {
            lock (_syncRoot)
            {
                EnsureReady();

                var current = _blackMagic.ReadUInt(baseAddr);
                if (offsets == null || offsets.Length == 0)
                {
                    return current;
                }

                foreach (var offset in offsets)
                {
                    current = _blackMagic.ReadUInt(unchecked(current + (uint)offset));
                }

                return current;
            }
        }

        public void Dispose()
        {
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                TryInvokeBlackMagicClose();
                _attachedProcess?.Dispose();
                _attachedProcess = null;
                _disposed = true;
            }

            GC.SuppressFinalize(this);
        }

        private void Attach()
        {
            lock (_syncRoot)
            {
                ThrowIfDisposed();

                _attachedProcess = Process.GetProcessesByName(_processName).FirstOrDefault();
                if (_attachedProcess == null)
                {
                    throw new InvalidOperationException(
                        $"Process '{_processName}' was not found. Start the game first, then retry.");
                }

                try
                {
                    _blackMagic.OpenProcessAndThread(_attachedProcess);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Failed to attach BlackMagic to process '{_processName}' (PID {_attachedProcess.Id}).", ex);
                }
            }
        }

        private void EnsureReady()
        {
            ThrowIfDisposed();

            if (_attachedProcess == null || _attachedProcess.HasExited)
            {
                throw new InvalidOperationException($"Process '{_processName}' is not attached or has exited.");
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(MemoryReader));
            }
        }

        private void TryInvokeBlackMagicClose()
        {
            try
            {
                var type = _blackMagic.GetType();
                var methods = new[] { "CloseProcessAndThread", "CloseProcess", "CloseThread" };

                foreach (var methodName in methods)
                {
                    var method = type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);
                    if (method != null)
                    {
                        method.Invoke(_blackMagic, null);
                        return;
                    }
                }
            }
            catch
            {
                // Intentionally swallow cleanup exceptions during dispose.
            }
        }
    }
}
