using Mixer.Audio.Interop;
using Mixer.Core.Apps;
using Mixer.Core.Sys;

namespace Mixer.Tests;

/// <summary>
/// Модель IAudioPolicyConfig по данным этапа 0: привязка хранится за exe (а не за PID), процесс без аудиоконтекста
/// отвечает E_INVALIDARG и на Set, и на Get.
/// </summary>
internal sealed class FakePolicy(Func<ProcessSnapshot> procs) : IAppEndpointPolicy
{
    public readonly Dictionary<(string Exe, ERole Role), string> Bindings = new();
    public readonly HashSet<uint> NoContext = [];
    public readonly List<(uint Pid, ERole Role, string? Endpoint)> Sets = [];
    public Action<uint>? BeforeSet;
    public int? SetError;
    /// <summary>Get возвращает не то, что поставили (API сломался в новой сборке).</summary>
    public bool LieOnGet;

    private string Exe(uint pid) => ProcessSnapshot.NormalizeExe(procs().ExeOf(pid));

    public int Set(uint pid, ERole role, string? endpointId)
    {
        if (NoContext.Contains(pid)) return Hr.E_INVALIDARG;
        BeforeSet?.Invoke(pid);
        Sets.Add((pid, role, endpointId));
        if (SetError is { } e) return e;
        if (endpointId is null) Bindings.Remove((Exe(pid), role));
        else Bindings[(Exe(pid), role)] = endpointId;
        return 0;
    }

    public int Get(uint pid, ERole role, out string? endpointId)
    {
        endpointId = null;
        if (NoContext.Contains(pid)) return Hr.E_INVALIDARG;
        endpointId = LieOnGet ? "{other}" : Bindings.GetValueOrDefault((Exe(pid), role));
        return 0;
    }

    public bool IsBound(string exe) => TakeOverManager.Roles.All(r => Bindings.ContainsKey((exe, r)));
}

internal sealed class FakeJournal : IBindingJournal
{
    public readonly List<string> List = [];
    public bool Fail;
    public int Writes;
    public IReadOnlyCollection<string> Apps => List;

    public void Add(string exe)
    {
        if (Fail) throw new IOException("диск полон");
        List.Add(exe);
        Writes++;
    }

    public void Remove(string exe)
    {
        if (Fail) throw new IOException("диск полон");
        List.RemoveAll(e => e.Equals(exe, StringComparison.OrdinalIgnoreCase));
        Writes++;
    }
}

public class TakeOverTests
{
    private const string Sink = World.Sink;

    private ProcInfo[] _procs = World.BaseProcs;
    private AudioSessionInfo[] _sessions = [World.Session(102), World.Session(201)];
    private readonly FakePolicy _policy;
    private readonly FakeJournal _journal = new();
    private readonly TakeOverManager _mgr;

    public TakeOverTests()
    {
        _policy = new FakePolicy(() => new ProcessSnapshot(_procs, World.Self));
        _mgr = new TakeOverManager(_policy, _journal);
    }

    private AppSnapshot W => World.Make(_procs, _sessions);

    private void Run(params string[] desired) => _mgr.Reconcile(desired, Sink, W);

    private void Without(string exe) => _procs = _procs.Where(p => !p.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase)).ToArray();

    [Fact]
    public void Binds_all_roles_on_session_pids_and_roots_after_writing_the_journal()
    {
        _policy.BeforeSet = _ => Assert.Contains("discord.exe", _journal.List); // журнал — до Set
        Run("Discord");
        Assert.True(_policy.IsBound("discord.exe"));
        Assert.Equal([100u, 102u, 300u], _policy.Sets.Select(s => s.Pid).Distinct().Order());
        Assert.Equal(9, _policy.Sets.Count); // 3 PID × 3 роли
        Assert.Equal(BindingState.Bound, _mgr.Status["discord.exe"].State);

        // повторный такт ничего не трогает: всё уже стоит
        Run("discord.exe");
        Assert.Equal(9, _policy.Sets.Count);
        Assert.Equal(1, _journal.Writes);
    }

    [Fact]
    public void Journal_failure_blocks_binding()
    {
        _journal.Fail = true;
        Run("discord");
        Assert.Empty(_policy.Sets);
        Assert.Equal(BindingState.Failed, _mgr.Status["discord.exe"].State);
        Assert.Contains("журнал", _mgr.Status["discord.exe"].Message);
    }

    [Fact]
    public void Process_without_audio_context_is_not_an_error_and_is_retried()
    {
        foreach (uint p in new uint[] { 100, 101, 102, 300 }) _policy.NoContext.Add(p);
        Run("discord");
        Assert.Equal(BindingState.WaitingAudio, _mgr.Status["discord.exe"].State);
        Assert.False(_policy.IsBound("discord.exe"));

        _policy.NoContext.Remove(102); // процесс открыл звук
        Run("discord");
        Assert.Equal(BindingState.Bound, _mgr.Status["discord.exe"].State);
        Assert.True(_policy.IsBound("discord.exe"));
    }

    [Fact]
    public void Turning_take_over_off_resets_and_clears_journal()
    {
        Run("discord");
        Run(); // узел удалён или «Забрать себе» выключен
        Assert.False(_policy.IsBound("discord.exe"));
        Assert.Empty(_journal.List);
        Assert.False(_mgr.Status.ContainsKey("discord.exe"));
    }

    [Fact]
    public void After_crash_reset_waits_for_the_app_and_happens_when_it_starts()
    {
        // прошлый запуск упал: в журнале Discord, в Windows привязка осталась, Discord сейчас не запущен
        _journal.List.Add("discord.exe");
        foreach (var r in TakeOverManager.Roles) _policy.Bindings[("discord.exe", r)] = Sink;
        Without("discord.exe");
        Run();
        Assert.Equal(BindingState.ResetPending, _mgr.Status["discord.exe"].State);
        Assert.Contains("discord.exe", _journal.List);

        // Discord запустился, но к звуку ещё не обращался: сброс невозможен (E_INVALIDARG)
        _procs = World.BaseProcs;
        _policy.NoContext.UnionWith([100u, 101u, 102u, 300u]);
        Run();
        Assert.Equal(BindingState.ResetPending, _mgr.Status["discord.exe"].State);

        _policy.NoContext.Clear();
        Run();
        Assert.False(_policy.IsBound("discord.exe"));
        Assert.Empty(_journal.List);
    }

    [Fact]
    public void Journaled_app_still_in_graph_is_simply_bound_again()
    {
        _journal.List.Add("discord.exe");
        Run("discord");
        Assert.True(_policy.IsBound("discord.exe"));
        Assert.Equal(["discord.exe"], _journal.List);
        Assert.Equal(0, _journal.Writes);
    }

    [Fact]
    public void Normal_exit_resets_running_apps_and_keeps_the_rest()
    {
        Run("discord", "chrome");
        Without("chrome.exe");
        _mgr.ReleaseAll(W);
        Assert.False(_policy.IsBound("discord.exe"));
        Assert.Equal(["chrome.exe"], _journal.List); // Chrome не запущен — снимется при следующем запуске микшера
        Assert.Equal(BindingState.ResetPending, _mgr.Status["chrome.exe"].State);
    }

    [Fact]
    public void Restarted_app_is_bound_on_new_pids()
    {
        Run("chrome");
        int before = _policy.Sets.Count;
        _procs = [.. _procs.Where(p => p.Exe != "chrome.exe"), new ProcInfo(700, 1, "chrome.exe"), new ProcInfo(701, 700, "chrome.exe")];
        _sessions = [World.Session(701)];
        Run("chrome");
        Assert.Equal([700u, 701u], _policy.Sets.Skip(before).Select(s => s.Pid).Distinct().Order());
        Assert.Equal(BindingState.Bound, _mgr.Status["chrome.exe"].State);
    }

    [Fact]
    public void Sink_change_rebinds()
    {
        Run("chrome");
        _mgr.Reconcile(["chrome"], "{0.0.0.00000000}.{other-sink}", W);
        Assert.All(TakeOverManager.Roles, r => Assert.Equal("{0.0.0.00000000}.{other-sink}", _policy.Bindings[("chrome.exe", r)]));
    }

    [Fact]
    public void No_usable_sink_releases_and_explains()
    {
        Run("chrome");
        _mgr.Reconcile(["chrome"], null, W, "«Voicemeeter In 5» не прошла проверку");
        Assert.False(_policy.IsBound("chrome.exe"));
        Assert.Empty(_journal.List);
        Assert.Equal(BindingState.Unavailable, _mgr.Status["chrome.exe"].State);
        Assert.Contains("Voicemeeter", _mgr.Status["chrome.exe"].Message);
    }

    [Fact]
    public void Set_error_is_reported_and_retried()
    {
        _policy.SetError = unchecked((int)0x80070005); // E_ACCESSDENIED: процесс с правами администратора
        Run("chrome");
        Assert.Equal(BindingState.Failed, _mgr.Status["chrome.exe"].State);
        Assert.Contains("E_ACCESSDENIED", _mgr.Status["chrome.exe"].Message);
        Assert.Contains("chrome.exe", _journal.List); // журнал уже записан: привязка могла встать частично

        _policy.SetError = null;
        Run("chrome");
        Assert.Equal(BindingState.Bound, _mgr.Status["chrome.exe"].State);
    }

    [Fact]
    public void Mismatched_get_is_a_failure()
    {
        _policy.LieOnGet = true;
        Run("chrome");
        Assert.Equal(BindingState.Failed, _mgr.Status["chrome.exe"].State);
        Assert.Contains("Get", _mgr.Status["chrome.exe"].Message);
    }

    [Fact]
    public void Session_left_on_old_device_asks_to_restart_playback()
    {
        Run("chrome"); // сессия 201 на Headphones, а заглушка — CABLE Input
        Assert.True(_mgr.Status["chrome.exe"].PlaysDirect);
        _sessions = [World.Session(201, Sink)];
        Run("chrome");
        Assert.False(_mgr.Status["chrome.exe"].PlaysDirect);
    }

    [Fact]
    public void Not_running_app_is_journaled_only_when_bound()
    {
        Without("discord.exe");
        Run("discord");
        Assert.Equal(BindingState.NotRunning, _mgr.Status["discord.exe"].State);
        Assert.Empty(_journal.List);
        Assert.Empty(_policy.Sets);
    }
}
