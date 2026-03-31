using System;
using System.Runtime.InteropServices;

public class GameInput
{
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    public static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(80);
        var down = new INPUT[1];
        down[0].type = 0;
        down[0].u.mi.dwFlags = 0x0002;
        SendInput(1, down, Marshal.SizeOf(typeof(INPUT)));
        System.Threading.Thread.Sleep(50);
        var up = new INPUT[1];
        up[0].type = 0;
        up[0].u.mi.dwFlags = 0x0004;
        SendInput(1, up, Marshal.SizeOf(typeof(INPUT)));
    }

    public static void PressKey(ushort vk, ushort scan)
    {
        var inputs = new INPUT[2];
        inputs[0].type = 1;
        inputs[0].u.ki.wVk = vk;
        inputs[0].u.ki.wScan = scan;
        inputs[1].type = 1;
        inputs[1].u.ki.wVk = vk;
        inputs[1].u.ki.wScan = scan;
        inputs[1].u.ki.dwFlags = 0x0002;
        SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
    }

    public static void TypeUnicode(char c)
    {
        var inputs = new INPUT[2];
        inputs[0].type = 1;
        inputs[0].u.ki.wScan = (ushort)c;
        inputs[0].u.ki.dwFlags = 0x0004;
        inputs[1].type = 1;
        inputs[1].u.ki.wScan = (ushort)c;
        inputs[1].u.ki.dwFlags = 0x0004 | 0x0002;
        SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
    }

    public static void TypeString(string s)
    {
        foreach (char c in s)
        {
            TypeUnicode(c);
            System.Threading.Thread.Sleep(30);
        }
    }

    public static void TypeTab() { PressKey(0x09, 0x0F); }
    public static void TypeEnter() { PressKey(0x0D, 0x1C); }
    public static void TypeEscape() { PressKey(0x1B, 0x01); }
}
