using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.PluginSystem.Tests;

public class ApiServerControllerTests
{
    [Fact]
    public void Initialize_StartsServerWhenApiEnabled()
    {
        var settings = new FakeSettingsService(new AppSettings { ApiServerEnabled = true, ApiServerPort = 9901 });
        var server = new FakeLocalApiServer();
        var controller = new ApiServerController(server, settings);

        controller.Initialize();

        Assert.True(server.IsRunning);
        Assert.Equal(9901, server.Port);
        Assert.Equal(9901, controller.ActivePort);
        Assert.Null(controller.ErrorMessage);
    }

    [Fact]
    public void SettingsChange_RestartsOnPortChangeAndStopsWhenDisabled()
    {
        var settings = new FakeSettingsService(new AppSettings { ApiServerEnabled = true, ApiServerPort = 9901 });
        var server = new FakeLocalApiServer();
        var controller = new ApiServerController(server, settings);
        controller.Initialize();

        settings.Save(settings.Current with { ApiServerPort = 9902 });

        Assert.True(server.IsRunning);
        Assert.Equal(9902, server.Port);
        Assert.Equal(2, server.StartCalls);
        Assert.Equal(1, server.StopCalls);

        settings.Save(settings.Current with { ApiServerEnabled = false });

        Assert.False(server.IsRunning);
        Assert.Null(controller.ActivePort);
        Assert.Equal(2, server.StopCalls);
    }

    [Fact]
    public void SettingsChange_ReportsStartError()
    {
        var settings = new FakeSettingsService(new AppSettings { ApiServerEnabled = true, ApiServerPort = 9901 });
        var server = new FakeLocalApiServer { StartError = new InvalidOperationException("port busy") };
        var controller = new ApiServerController(server, settings);

        controller.Initialize();

        Assert.False(server.IsRunning);
        Assert.Null(controller.ActivePort);
        Assert.Equal("port busy", controller.ErrorMessage);
    }

    private sealed class FakeLocalApiServer : ILocalApiServer
    {
        public bool IsRunning { get; private set; }
        public int? Port { get; private set; }
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public Exception? StartError { get; init; }

        public void Start(int port)
        {
            StartCalls++;
            if (StartError is not null)
                throw StartError;

            Port = port;
            IsRunning = true;
        }

        public void Stop()
        {
            StopCalls++;
            IsRunning = false;
            Port = null;
        }
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public FakeSettingsService(AppSettings current) => Current = current;

        public AppSettings Current { get; private set; }
        public event Action<AppSettings>? SettingsChanged;

        public AppSettings Load() => Current;

        public void Save(AppSettings settings)
        {
            Current = settings;
            SettingsChanged?.Invoke(settings);
        }
    }
}
