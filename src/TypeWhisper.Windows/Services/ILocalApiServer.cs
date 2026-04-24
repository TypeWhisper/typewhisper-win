namespace TypeWhisper.Windows.Services;

public interface ILocalApiServer
{
    bool IsRunning { get; }
    void Start(int port);
    void Stop();
}
