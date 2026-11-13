using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// 本地 WAV 循环播放，并且能把"已经播到第几个采样"报出来。
// 弹弹弹的相位就是拿这个数按 BPM 算的，所以节奏和音频永远对得上，
// 不会像计时器那样越跑越偏。
internal sealed class WaveAudio : IDisposable
{
    const uint CALLBACK_NULL = 0;
    const uint WHDR_DONE = 0x00000001;
    const uint TIME_SAMPLES = 0x00000002;

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WAVEFORMATEX
    {
        public ushort FormatTag, Channels;
        public uint SamplesPerSec, AvgBytesPerSec;
        public ushort BlockAlign, BitsPerSample, ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WAVEHDR
    {
        public IntPtr Data;
        public uint Length, Recorded;
        public IntPtr User;
        public uint Flags, Loops;
        public IntPtr Next, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MMTIME { public uint Type, Sample, Pad; }

    [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr device, uint deviceId, ref WAVEFORMATEX format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr device, IntPtr header, int size);
    [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr device, IntPtr header, int size);
    [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr device);
    [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr device);
    [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr device, IntPtr header, int size);
    [DllImport("winmm.dll")] static extern int waveOutGetPosition(IntPtr device, ref MMTIME time, int size);

    IntPtr device;
    byte[] pcm;
    int blockBytes, blockAlign, sampleRate;
    long totalFrames, writeFrame;
    List<IntPtr> headers = new List<IntPtr>();
    List<IntPtr> buffers = new List<IntPtr>();
    Thread worker;
    volatile bool running;

    public double PositionSeconds()
    {
        if (device == IntPtr.Zero) return 0;
        MMTIME time = new MMTIME { Type = TIME_SAMPLES };
        if (waveOutGetPosition(device, ref time, Marshal.SizeOf(typeof(MMTIME))) != 0) return 0;
        long samples = time.Sample;
        return (double)(samples % totalFrames) / sampleRate;
    }

    // 从开始到现在一共播了多少秒（不回绕），用来判断整首放完没有
    public double PlayedSeconds()
    {
        if (device == IntPtr.Zero) return 0;
        MMTIME time = new MMTIME { Type = TIME_SAMPLES };
        if (waveOutGetPosition(device, ref time, Marshal.SizeOf(typeof(MMTIME))) != 0) return 0;
        return (double)time.Sample / sampleRate;
    }

    public double DurationSeconds
    {
        get { return sampleRate > 0 ? (double)totalFrames / sampleRate : 0; }
    }

    public void Start(string path)
    {
        ReadWav(path);
        WAVEFORMATEX format = new WAVEFORMATEX();
        format.FormatTag = 1;
        format.Channels = (ushort)(blockAlign / 2);
        format.SamplesPerSec = (uint)sampleRate;
        format.BlockAlign = (ushort)blockAlign;
        format.BitsPerSample = 16;
        format.AvgBytesPerSec = (uint)(sampleRate * blockAlign);
        format.Channels = (ushort)(blockAlign / 2);

        if (waveOutOpen(out device, 0, ref format, IntPtr.Zero, IntPtr.Zero, CALLBACK_NULL) != 0)
            throw new InvalidOperationException("系统无法打开音频输出设备（" + Marshal.GetLastWin32Error() + "）");

        blockBytes = blockAlign * sampleRate / 4;          // 每块 250 毫秒
        blockBytes -= blockBytes % blockAlign;
        for (int i = 0; i < 8; i++)
        {
            IntPtr buffer = Marshal.AllocHGlobal(blockBytes);
            WAVEHDR header = new WAVEHDR { Data = buffer, Length = (uint)blockBytes };
            IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WAVEHDR)));
            Marshal.StructureToPtr(header, ptr, false);
            if (waveOutPrepareHeader(device, ptr, Marshal.SizeOf(typeof(WAVEHDR))) != 0)
                throw new InvalidOperationException("音频缓冲准备失败");
            buffers.Add(buffer); headers.Add(ptr);
        }
        writeFrame = 0;
        running = true;
        for (int i = 0; i < headers.Count; i++) Fill(i);
        worker = new Thread(delegate()
        {
            while (running)
            {
                for (int i = 0; i < headers.Count && running; i++)
                {
                    WAVEHDR header = (WAVEHDR)Marshal.PtrToStructure(headers[i], typeof(WAVEHDR));
                    if ((header.Flags & WHDR_DONE) == 0) continue;
                    header.Flags &= ~WHDR_DONE;
                    Marshal.StructureToPtr(header, headers[i], false);
                    Fill(i);
                }
                Thread.Sleep(15);
            }
        });
        worker.IsBackground = true;
        worker.Start();
    }

    // 把下一块数据（自动回绕到开头）塞进第 index 个缓冲并送出去。
    void Fill(int index)
    {
        long offset = writeFrame % totalFrames;
        int frames = blockBytes / blockAlign;
        byte[] chunk = new byte[blockBytes];
        int copied = 0;
        while (copied < blockBytes)
        {
            long start = (offset + copied / blockAlign) % totalFrames;
            long available = totalFrames - start;
            int want = blockBytes - copied;
            long maxFrames = available;
            if (maxFrames * blockAlign < want) want = (int)(maxFrames * blockAlign);
            Buffer.BlockCopy(pcm, (int)(start * blockAlign), chunk, copied, want);
            copied += want;
        }
        Marshal.Copy(chunk, 0, buffers[index], blockBytes);
        WAVEHDR header = (WAVEHDR)Marshal.PtrToStructure(headers[index], typeof(WAVEHDR));
        header.Length = (uint)blockBytes;
        Marshal.StructureToPtr(header, headers[index], false);
        waveOutWrite(device, headers[index], Marshal.SizeOf(typeof(WAVEHDR)));
        writeFrame += frames;
    }

    void ReadWav(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("缺少本地音频 reference-audio.wav", path);
        using (FileStream file = File.OpenRead(path))
        {
            byte[] head = new byte[12];
            if (file.Read(head, 0, 12) != 12 || Encoding.ASCII.GetString(head, 0, 4) != "RIFF" || Encoding.ASCII.GetString(head, 8, 4) != "WAVE")
                throw new InvalidDataException("不是有效的 WAV 文件");
            ushort channels = 0, bits = 0;
            int rate = 0;
            byte[] data = null;
            while (file.Position < file.Length)
            {
                byte[] id = new byte[8];
                if (file.Read(id, 0, 8) != 8) break;
                int size = BitConverter.ToInt32(id, 4);
                string name = Encoding.ASCII.GetString(id, 0, 4);
                if (name == "fmt ")
                {
                    byte[] fmt = new byte[Math.Min(size, 40)];
                    file.Read(fmt, 0, fmt.Length);
                    channels = BitConverter.ToUInt16(fmt, 2);
                    rate = BitConverter.ToInt32(fmt, 4);
                    bits = BitConverter.ToUInt16(fmt, 14);
                    if (size > fmt.Length) file.Position += size - fmt.Length;
                }
                else if (name == "data")
                {
                    data = new byte[size];
                    int read = 0;
                    while (read < size)
                    {
                        int got = file.Read(data, read, size - read);
                        if (got <= 0) break;
                        read += got;
                    }
                    break;
                }
                else file.Position += size + (size & 1);
            }
            if (data == null || rate <= 0 || channels == 0) throw new InvalidDataException("WAV 缺少 fmt 或 data 段");
            if (bits != 16) throw new InvalidDataException("只支持 16 位 PCM 的 WAV（当前 " + bits + " 位）");
            blockAlign = channels * 2;
            sampleRate = rate;
            pcm = data;
            totalFrames = data.Length / blockAlign;
        }
    }

    public void Stop()
    {
        running = false;
        if (worker != null) { try { worker.Join(150); } catch (Exception) { } worker = null; }
        if (device != IntPtr.Zero)
        {
            waveOutReset(device);
            foreach (IntPtr header in headers) waveOutUnprepareHeader(device, header, Marshal.SizeOf(typeof(WAVEHDR)));
            waveOutClose(device);
            device = IntPtr.Zero;
        }
        foreach (IntPtr header in headers) Marshal.FreeHGlobal(header);
        foreach (IntPtr buffer in buffers) Marshal.FreeHGlobal(buffer);
        headers.Clear(); buffers.Clear();
    }

    public void Dispose() { Stop(); }
}
