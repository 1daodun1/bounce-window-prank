using System;
using System.Runtime.InteropServices;

// 虚拟按键输出：给当前焦点控件发字符，并临时摘掉该窗口的输入法，避免数字被当成拼音截走
internal static class KeySpam
{
    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [DllImport("user32.dll")] static extern byte MapVirtualKey(uint code, uint mapType);
    [DllImport("imm32.dll")] static extern IntPtr ImmAssociateContext(IntPtr hwnd, IntPtr context);

    const uint WM_CHAR = 0x0102;
    const uint KEYEVENTF_KEYUP = 0x0002;

    static string text = "78917891";
    static bool on;
    static int cursor;
    static int sent;
    static IntPtr imeWindow = IntPtr.Zero;
    static IntPtr imeContext = IntPtr.Zero;

    public static bool On { get { return on; } set { if (!value) ReleaseIme(); on = value; } }
    public static int Sent { get { return sent; } }

    public static void Configure(string content)
    {
        if (content != null && content.Length > 0) text = content;
        cursor = 0;
        sent = 0;
    }

    public static void TypeOne()
    {
        if (!on || text.Length == 0) return;
        char c = text[cursor];
        cursor = (cursor + 1) % text.Length;
        if (c == '\r' || c == '\n') return;

        if (SendChar(c)) sent++;
    }

    static bool SendChar(char c)
    {
        GUITHREADINFO info = new GUITHREADINFO();
        info.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
        if (!GetGUIThreadInfo(0, ref info) || info.hwndFocus == IntPtr.Zero)
        {
            ReleaseIme();
            return Key(c);
        }

        // 换了个焦点控件：把上一个窗口的输入法还回去，再摘掉新窗口的
        if (info.hwndFocus != imeWindow)
        {
            ReleaseIme();
            imeContext = ImmAssociateContext(info.hwndFocus, IntPtr.Zero);
            imeWindow = info.hwndFocus;
        }

        // 两条都发：WM_CHAR 给不吃输入法的程序，物理键码给浏览器这类只认键盘消息的程序
        PostMessage(info.hwndFocus, WM_CHAR, (IntPtr)c, (IntPtr)1);
        Key(c);
        return true;
    }

    static bool Key(char c)
    {
        if (c < '0' || c > '9') return false;
        byte vk = (byte)(0x30 + (c - '0'));
        byte scan = MapVirtualKey(vk, 0);
        keybd_event(vk, scan, 0, IntPtr.Zero);
        keybd_event(vk, scan, KEYEVENTF_KEYUP, IntPtr.Zero);
        return true;
    }

    public static void ReleaseIme()
    {
        if (imeWindow != IntPtr.Zero)
        {
            ImmAssociateContext(imeWindow, imeContext);
            imeWindow = IntPtr.Zero;
            imeContext = IntPtr.Zero;
        }
    }
}
