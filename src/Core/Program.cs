using System;

namespace TalosForge.Core
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("TalosForge initializing... Custodem finge!");

            try
            {
                var reader = MemoryReader.Instance;
                if (!reader.Attach())
                {
                    Console.WriteLine("WoW not found");
                    return;
                }

                if (reader.BaseAddress == IntPtr.Zero)
                {
                    Console.WriteLine("Attach failed: BaseAddress is zero");
                    return;
                }

                Console.WriteLine(
                    string.Format("Attach succeeded. BaseAddress: 0x{0:X}", reader.BaseAddress.ToInt64()));

                var clientConnection = reader.ReadPointer(
                    IntPtr.Add(reader.BaseAddress, Offsets.STATIC_CLIENT_CONNECTION));
                Console.WriteLine(
                    string.Format(
                        "STATIC_CLIENT_CONNECTION pointer: 0x{0:X}",
                        clientConnection.ToInt64()));
            }
            catch (Exception ex)
            {
                Console.WriteLine(string.Format("Attach failed: {0}", ex.Message));
            }
        }
    }
}
