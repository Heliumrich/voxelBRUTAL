using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VoxelBrutal.Demo;

/// <summary>
/// Deadline pacing before input sampling. Uses a private high-resolution Windows waitable timer,
/// then a short spin; it does not change the machine's global timer resolution or accumulate debt.
/// </summary>
internal sealed class FramePacer : IDisposable
{
    private readonly nint timer;
    private double nextStart;
    private int previousLimit;
    public string TimerKind => timer != 0 ? "High-resolution waitable timer" : "Sleep/spin fallback";

    public FramePacer()
    {
        if (OperatingSystem.IsWindows()) timer = CreateWaitableTimerExW(0, 0, 2, 0x00100002); // SYNCHRONIZE | TIMER_MODIFY_STATE
    }

    public void Wait(int framesPerSecond, bool uncapped = false)
    {
        if (framesPerSecond is < 30 or > 360) throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        double now = Stopwatch.GetTimestamp();
        double period = Stopwatch.Frequency / (double)framesPerSecond;
        if (uncapped) { nextStart = now; previousLimit = 0; return; }
        if (previousLimit != framesPerSecond || nextStart == 0)
        { previousLimit = framesPerSecond; nextStart = now; }
        // A slow frame starts immediately. Never run a catch-up burst after a resize or breakpoint.
        if (now >= nextStart) { nextStart = now + period; return; }
        while ((now = Stopwatch.GetTimestamp()) < nextStart)
        {
            double seconds = (nextStart - now) / Stopwatch.Frequency;
            const double spinSeconds = .0002;
            if (seconds > .0006 && timer != 0)
            {
                long due = -Math.Max(1, (long)((seconds - spinSeconds) * 10_000_000));
                if (SetWaitableTimer(timer, in due, 0, 0, 0, false)) WaitForSingleObject(timer, 1000);
                else Thread.Sleep(1);
            }
            else if (seconds > .002) Thread.Sleep(Math.Max(1, (int)((seconds - .001) * 1000)));
            else Thread.SpinWait(32);
        }
        double scheduled = nextStart + period;
        double end = Stopwatch.GetTimestamp();
        nextStart = end > scheduled ? end + period : scheduled;
    }

    public void Dispose() { if (timer != 0) CloseHandle(timer); }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nint CreateWaitableTimerExW(nint attributes, nint name, uint flags, uint access);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(nint timer, in long dueTime, int period, nint completionRoutine,
        nint argument, [MarshalAs(UnmanagedType.Bool)] bool resume);
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
}

/// <summary>Actual start-to-start frame interval, including pacing and CPU/GPU waits.</summary>
internal sealed class FrameMetrics
{
    private double seconds;
    private int frames;
    public double FramesPerSecond { get; private set; }
    public double FrameMilliseconds { get; private set; }

    public void Add(double intervalSeconds)
    {
        if (!(intervalSeconds > 0) || !double.IsFinite(intervalSeconds)) return;
        seconds += intervalSeconds; frames++;
        if (seconds >= .25 || FramesPerSecond == 0)
        {
            FrameMilliseconds = seconds * 1000 / frames;
            FramesPerSecond = frames / seconds;
            seconds = 0; frames = 0;
        }
    }
}
