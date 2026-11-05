using ADHDWindows.Core.Hosting;
using ADHDWindows.Core.Logging;
using ADHDWindows.Core.Modules;
using ADHDWindows.Core.Safety;

namespace ADHDWindows.Tests;

public sealed class RecoveryTests : IDisposable
{
    private const string Kind = "test.kind";

    private readonly TempDirectory _temp = new();
    private readonly RecordingLog _log = new();
    private readonly FakeRestorer _restorer = new();
    private readonly ChangeJournal _journal;

    public RecoveryTests() => _journal = new ChangeJournal(_temp.Combine("recovery.json"), _log);

    public void Dispose() => _temp.Dispose();

    private RecoveryService CreateService() => new(_journal, () => [_restorer], _log);

    [Fact]
    public void Record_reaches_the_disk_before_it_returns()
    {
        CreateService().For("module").Record(Kind, "data");

        var change = Assert.Single(new ChangeJournal(_temp.Combine("recovery.json"), _log).Read());
        Assert.Equal(Kind, change.Kind);
        Assert.Equal("data", change.Data);
        Assert.Equal("module", change.Module);
    }

    [Fact]
    public void Failed_record_tells_the_module_not_to_apply_the_change()
    {
        // Папка на месте файла: записать реестр невозможно.
        var path = _temp.Combine("blocked");
        Directory.CreateDirectory(path);
        var service = new RecoveryService(new ChangeJournal(path, _log), () => [], _log);

        Assert.ThrowsAny<Exception>(() => service.For("module").Record(Kind, "data"));
    }

    [Fact]
    public void Disposing_the_mark_removes_the_record_once()
    {
        var service = CreateService();
        var first = service.For("module").Record(Kind, "1");
        service.For("module").Record(Kind, "2");

        first.Dispose();
        first.Dispose();

        Assert.Equal("2", Assert.Single(_journal.Read()).Data);
    }

    [Fact]
    public void Changes_are_restored_in_reverse_order()
    {
        var service = CreateService();
        service.For("module").Record(Kind, "1");
        service.For("module").Record(Kind, "2");
        service.For("module").Record(Kind, "3");

        service.RestoreAll();

        Assert.Equal(["3", "2", "1"], _restorer.Restored);
        Assert.Empty(_journal.Read());
    }

    [Fact]
    public void Restoring_a_session_keeps_changes_of_a_newer_session()
    {
        var old = CreateService();
        var current = CreateService();
        old.For("module").Record(Kind, "old");
        current.For("module").Record(Kind, "new");

        old.RestoreSession(old.Session);

        Assert.Equal(["old"], _restorer.Restored);
        Assert.Equal("new", Assert.Single(_journal.Read()).Data);
    }

    [Fact]
    public void Failed_restore_stays_and_does_not_block_the_others()
    {
        var service = CreateService();
        service.For("module").Record(Kind, "1");
        service.For("module").Record(Kind, "2");
        _restorer.FailOn = "2";

        service.RestoreAll();

        Assert.Equal(["1"], _restorer.Restored);
        var left = Assert.Single(_journal.Read());
        Assert.Equal("2", left.Data);
        Assert.Equal(1, left.Attempts);
    }

    [Fact]
    public void Hopeless_restore_is_dropped_after_the_limit()
    {
        var service = CreateService();
        service.For("module").Record(Kind, "1");
        _restorer.FailOn = "1";

        for (var i = 0; i < RecoveryService.MaxAttempts; i++)
        {
            Assert.Single(_journal.Read());
            service.RestoreAll();
        }

        Assert.Empty(_journal.Read());
    }

    [Fact]
    public void Change_without_a_restorer_is_kept_for_the_next_attempt()
    {
        var service = CreateService();
        service.For("module").Record("removed.module", "1");

        service.RestoreAll();

        Assert.Equal(1, Assert.Single(_journal.Read()).Attempts);
        Assert.NotEmpty(_log.Errors);
    }

    [Fact]
    public void Restorers_are_not_loaded_while_there_is_nothing_to_restore()
    {
        var loaded = false;
        var service = new RecoveryService(
            _journal,
            () =>
            {
                loaded = true;
                return [];
            },
            _log);

        service.RestoreAll();

        Assert.False(loaded);
    }

    [Fact]
    public void Damaged_journal_falls_back_to_the_backup()
    {
        var service = CreateService();
        service.For("module").Record(Kind, "1");
        service.For("module").Record(Kind, "2");
        File.WriteAllText(_temp.Combine("recovery.json"), "{ не json");

        // В копии лежит предыдущая версия файла.
        Assert.Equal("1", Assert.Single(_journal.Read()).Data);
    }

    [Fact]
    public void Parallel_writers_do_not_lose_records()
    {
        // Отдельные объекты, как у двух процессов: общий у них только файл и блокировка.
        Parallel.For(0, 40, index => new RecoveryService(
                new ChangeJournal(_temp.Combine("recovery.json"), _log),
                () => [],
                new NullLog())
            .For("module").Record(Kind, index.ToString()));

        Assert.Equal(40, _journal.Read().Count);
    }

    [Fact]
    public void Restorer_from_a_module_folder_is_found()
    {
        var modules = _temp.Combine("modules");
        RepositoryRoot.DeploySampleModule(modules);

        var restorer = Assert.Single(ModuleLoader.LoadRestorers(modules, _log));

        Assert.Equal("sample.probe", restorer.Kind);
    }

    [Fact]
    public void Host_restores_leftovers_of_the_previous_run_before_modules_start()
    {
        var paths = new AppPaths(_temp.Combine("install"), _temp.Combine("user"));
        RepositoryRoot.DeploySampleModule(paths.ModulesDir);
        var token = $"test-{Guid.NewGuid():N}";
        ProbeRegistry.Apply(token);
        try
        {
            using (var crashed = new GameHost(paths, new AppOptions(true), _log))
            {
                crashed.Start();
                crashed.Recovery.For("sample").Record("sample.probe", token);

                // Программа «падает»: реестр остаётся, как при сбое.
                File.Copy(paths.RecoveryPath, paths.RecoveryPath + ".crash");
            }

            File.Copy(paths.RecoveryPath + ".crash", paths.RecoveryPath, overwrite: true);
            ProbeRegistry.Apply(token);

            using var next = new GameHost(paths, new AppOptions(true), _log);
            next.Start();

            Assert.False(ProbeRegistry.Exists(token));
            Assert.Empty(new ChangeJournal(paths.RecoveryPath, _log).Read());
        }
        finally
        {
            ProbeRegistry.Remove(token);
        }
    }

    [Fact]
    public void Host_restores_what_a_module_left_behind_on_stop()
    {
        var paths = new AppPaths(_temp.Combine("install"), _temp.Combine("user"));
        RepositoryRoot.DeploySampleModule(paths.ModulesDir);
        var token = $"test-{Guid.NewGuid():N}";
        ProbeRegistry.Apply(token);
        try
        {
            using (var host = new GameHost(paths, new AppOptions(true), _log))
            {
                host.Start();
                host.Recovery.For("sample").Record("sample.probe", token);
            }

            Assert.False(ProbeRegistry.Exists(token));
        }
        finally
        {
            ProbeRegistry.Remove(token);
        }
    }

    [Fact]
    public void Sample_module_applies_and_reverts_its_change()
    {
        var paths = new AppPaths(_temp.Combine("install"), _temp.Combine("user"));
        RepositoryRoot.DeploySampleModule(paths.ModulesDir);
        var token = $"test-{Guid.NewGuid():N}";
        try
        {
            using (var host = new GameHost(paths, new AppOptions(true), _log))
            {
                host.Start();
                host.Events.Publish(new RecoveryProbeRequested(token));

                Assert.True(ProbeRegistry.Exists(token));
                Assert.Single(new ChangeJournal(paths.RecoveryPath, _log).Read());
            }

            Assert.False(ProbeRegistry.Exists(token));
            Assert.Empty(new ChangeJournal(paths.RecoveryPath, _log).Read());
        }
        finally
        {
            ProbeRegistry.Remove(token);
        }
    }

    [Fact]
    public void Sample_probe_is_ignored_outside_developer_mode()
    {
        var paths = new AppPaths(_temp.Combine("install"), _temp.Combine("user"));
        RepositoryRoot.DeploySampleModule(paths.ModulesDir);
        var token = $"test-{Guid.NewGuid():N}";
        using var host = new GameHost(paths, new AppOptions(false), _log);
        host.Start();

        host.Events.Publish(new RecoveryProbeRequested(token));

        Assert.False(ProbeRegistry.Exists(token));
    }

    private sealed class FakeRestorer : ISystemRestorer
    {
        public List<string> Restored { get; } = [];

        public string? FailOn { get; set; }

        public string Kind => RecoveryTests.Kind;

        public void Restore(string data)
        {
            if (data == FailOn)
            {
                throw new InvalidOperationException("Сбой отката");
            }

            Restored.Add(data);
        }
    }

    private sealed class NullLog : ILog
    {
        public void Info(string message)
        {
        }

        public void Warn(string message)
        {
        }

        public void Error(string message, Exception? exception = null)
        {
        }
    }
}
