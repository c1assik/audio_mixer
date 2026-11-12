namespace Mixer.Plugins.Host;

/// <summary>
/// Job Object микшера с KILL_ON_JOB_CLOSE: все хосты живут в нём, и даже если микшер упадёт,
/// Windows завершит их вместе с последним дескриптором задания.
/// </summary>
internal static class HostJob
{
    private static readonly Lazy<IntPtr> s_job = new(Create);

    public static void Assign(System.Diagnostics.Process process)
    {
        if (Native.AssignProcessToJobObject(s_job.Value, process.Handle) == 0)
            throw Native.LastError("AssignProcessToJobObject");
    }

    private static unsafe IntPtr Create()
    {
        IntPtr job = Native.CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) throw Native.LastError("CreateJobObject");
        var info = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (Native.SetInformationJobObject(job, Native.JobObjectExtendedLimitInformation, ref info,
                (uint)sizeof(Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION)) == 0)
            throw Native.LastError("SetInformationJobObject");
        return job; // не закрываем: закроется вместе с процессом микшера и заберёт хосты
    }
}
