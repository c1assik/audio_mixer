namespace Mixer.Probe.Interop;

public static class Hr
{
    private static readonly Dictionary<uint, string> Names = new()
    {
        [0x00000000] = "S_OK",
        [0x00000001] = "S_FALSE",
        [0x80004001] = "E_NOTIMPL",
        [0x80004002] = "E_NOINTERFACE",
        [0x80004005] = "E_FAIL",
        [0x8000000E] = "E_ILLEGAL_METHOD_CALL",
        [0x80040154] = "REGDB_E_CLASSNOTREG",
        [0x80070005] = "E_ACCESSDENIED",
        [0x80070057] = "E_INVALIDARG",
        [0x8007000E] = "E_OUTOFMEMORY",
        [0x80070490] = "E_NOTFOUND",
        [0x88890001] = "AUDCLNT_E_NOT_INITIALIZED",
        [0x88890002] = "AUDCLNT_E_ALREADY_INITIALIZED",
        [0x88890003] = "AUDCLNT_E_WRONG_ENDPOINT_TYPE",
        [0x88890004] = "AUDCLNT_E_DEVICE_INVALIDATED",
        [0x88890008] = "AUDCLNT_E_UNSUPPORTED_FORMAT",
        [0x8889000A] = "AUDCLNT_E_DEVICE_IN_USE",
        [0x88890011] = "AUDCLNT_E_EVENTHANDLE_NOT_EXPECTED",
        [0x88890016] = "AUDCLNT_E_BUFFER_SIZE_ERROR",
        [0x88890021] = "AUDCLNT_E_INVALID_STREAM_FLAG",
        [0x08890001] = "AUDCLNT_S_BUFFER_EMPTY",
        [0x0889000D] = "AUDCLNT_S_NO_SINGLE_PROCESS",
    };

    public static string Describe(int hr)
    {
        uint u = unchecked((uint)hr);
        return Names.TryGetValue(u, out var name) ? $"0x{u:X8} {name}" : $"0x{u:X8}";
    }
}

public sealed class AudioException : Exception
{
    public AudioException(string operation, int hr)
        : base($"{operation}: {Hr.Describe(hr)}")
    {
        HResult = hr;
    }

    public AudioException(string message) : base(message)
    {
    }

    public static void Check(int hr, string operation)
    {
        if (hr < 0) throw new AudioException(operation, hr);
    }
}
